using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Infrastructure;

public sealed class TwilioPhoneProvider(PhoneService service) : IPhoneProvider
{
    public string Name => "twilio";
    public Task<JsonNode?> StartAsync(string owner, JsonObject request, CancellationToken ct) => service.ExecuteAsync(new(owner), "Phone.Call", request, ct);
    public Task<JsonNode?> ControlAsync(string owner, string callId, string operation, JsonObject args, CancellationToken ct)
    {
        if (operation is not ("Hangup" or "SendDtmf")) throw new JarvisException("Diese Steuerung ist im Twilio-Adapter nicht verfügbar.", 409);
        args = args.DeepClone().AsObject(); args["callId"] = callId;
        return service.ExecuteAsync(new(owner), "Phone." + operation, args, ct);
    }
    public async Task<JsonNode?> HealthAsync(string owner, CancellationToken ct) { await service.TestAsync(owner, ct); return new JsonObject { ["connected"] = true }; }
}

public sealed class SipPhoneProvider(Settings settings, IDocumentStore store, IHttpClientFactory clients, IConfiguration config) : IPhoneProvider
{
    public string Name => "sip";
    public async Task<JsonNode?> RequestAsync(HttpMethod method, string path, JsonObject? data, CancellationToken ct)
    {
        var token = config["SIP_SERVICE_TOKEN"];
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32) throw new JarvisException("SIP-Diensttoken fehlt. Installer erneut ausführen.", 409);
        using var client = clients.CreateClient("provider");
        using var request = new HttpRequestMessage(method, (config["SIP_SERVICE_URL"] ?? "http://jarvis-sip:8080").TrimEnd('/') + path);
        request.Headers.Add("X-Service-Token", token);
        if (data is not null) request.Content = JsonContent.Create(data);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException($"SIP-Dienst: HTTP {(int)response.StatusCode}. Kein automatischer Wahlwiederholungsversuch.", 502);
        return await response.Content.ReadFromJsonAsync<JsonNode>(ct);
    }
    public async Task<JsonNode?> HealthAsync(string owner, CancellationToken ct)
    {
        await settings.RequireAsync(owner, "sip", ct);
        return await RequestAsync(HttpMethod.Get, "/accounts?owner=" + Uri.EscapeDataString(owner), null, ct);
    }
    public async Task<JsonNode?> StartAsync(string owner, JsonObject request, CancellationToken ct)
    {
        await settings.RequireAsync(owner, "sip", ct);
        var cfg = await settings.GetAsync(owner, "sip", ct);
        var accounts = cfg["accounts"]?.Deserialize<List<SipAccount>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var number = request["number"]!.GetValue<string>();
        var accountId = request["accountId"]?.GetValue<string>() ?? PhonePolicy.Route(cfg, accounts, number,
            request["usage"]?.GetValue<string>() ?? "private", request["contactGroup"]?.GetValue<string>() ?? "",
            request["taskType"]?.GetValue<string>() ?? "call", request["localHour"]?.GetValue<int>() ?? 12);
        var health = await HealthAsync(owner, ct);
        bool Registered(string id) => health?["accounts"]?.AsArray().Any(a => a?["id"]?.GetValue<string>() == id && a?["registered"]?.GetValue<bool>() == true) == true;
        if (!Registered(accountId) && cfg["allowFallback"]?.GetValue<bool>() == true)
            accountId = cfg["fallbackAccount"]?.GetValue<string>() ?? accountId;
        if (!Registered(accountId)) throw new JarvisException("Gewähltes SIP-Konto nicht registriert.", 409);
        var account = accounts.FirstOrDefault(a => a.Id == accountId && a.Enabled && a.AllowedDirections.Contains("outbound"))
            ?? throw new JarvisException("SIP-Konto nicht für ausgehende Anrufe freigegeben.", 403);
        var id = request["callId"]!.GetValue<string>();
        var call = request.DeepClone().AsObject();
        call["provider"] = "sip"; call["sip_account"] = account.Id; call["direction"] = "outbound";
        call["status"] = "Calling"; call["started_at"] = DateTimeOffset.UtcNow.ToString("O"); call["recording"] = false;
        call["transcript"] = new JsonArray();
        await store.PutAsync(owner, "calls", id, call, ct);
        try { return await RequestAsync(HttpMethod.Post, "/calls", new() { ["owner"] = owner, ["callId"] = id, ["accountId"] = account.Id, ["number"] = number, ["maxDuration"] = request["max_duration"]!.DeepClone() }, ct); }
        catch { call["status"] = "Unknown"; call["result"] = "Call-start response missing; check SIP status before retrying."; await store.PutAsync(owner, "calls", id, call, CancellationToken.None); throw; }
    }
    public Task<JsonNode?> ControlAsync(string owner, string callId, string operation, JsonObject args, CancellationToken ct)
    {
        var body = args.DeepClone().AsObject(); body["owner"] = owner;
        return RequestAsync(HttpMethod.Post, "/calls/" + Uri.EscapeDataString(callId) + "/" + operation, body, ct);
    }
}
