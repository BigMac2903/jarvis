using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public sealed class OAuthService(Database db, Settings settings, Vault vault, IHttpClientFactory clients, IConfiguration config)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> Gates = new();
    private string Redirect(string provider) => config["PUBLIC_URL"]!.TrimEnd('/') + "/api/v1/oauth/" + provider + "/callback";
    public static (string Authorize,string Token,string Scope) Endpoints(string provider) => provider switch {
        "google" => ("https://accounts.google.com/o/oauth2/v2/auth","https://oauth2.googleapis.com/token","https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.compose"),
        "microsoft" => ("https://login.microsoftonline.com/common/oauth2/v2.0/authorize","https://login.microsoftonline.com/common/oauth2/v2.0/token","offline_access User.Read Calendars.ReadWrite Mail.Read Mail.Send Mail.ReadWrite"),
        _ => throw new JarvisException("OAuth-Provider unbekannt.")
    };
    public async Task<string> StartAsync(string owner,string provider,CancellationToken ct)
    {
        var endpoints = Endpoints(provider);
        await settings.RequireAsync(owner,provider,ct);
        var cfg = await settings.GetAsync(owner,provider,ct);
        var state = Crypto.Token(); var verifier = Crypto.Token();
        await db.ExecuteAsync("INSERT INTO oauth_states(state_hash,owner,provider,verifier) VALUES($1,$2,$3,$4)",ct,Crypto.Hash(state),owner,provider,vault.Encrypt(verifier,owner+":pkce"));
        var values = new Dictionary<string,string> {
            ["client_id"]=cfg["clientId"]?.GetValue<string>() ?? throw new JarvisException("Client-ID fehlt."),
            ["redirect_uri"]=Redirect(provider),["response_type"]="code",["scope"]=endpoints.Scope,["state"]=state,
            ["code_challenge"]=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_'),
            ["code_challenge_method"]="S256",["access_type"]="offline",["prompt"]="consent"
        };
        return endpoints.Authorize+"?"+string.Join("&",values.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)));
    }
    public async Task CompleteAsync(string provider,string state,string code,CancellationToken ct)
    {
        var rows = await db.QueryAsync("DELETE FROM oauth_states WHERE state_hash=$1 AND provider=$2 AND expires_at>now() RETURNING owner,verifier",ct,Crypto.Hash(state),provider);
        if (rows.Count != 1) throw new JarvisException("OAuth-State ungültig oder abgelaufen.",403);
        var owner=rows[0]["owner"]!.GetValue<string>();
        await settings.RequireAsync(owner,provider,ct);
        var cfg=await settings.GetAsync(owner,provider,ct);
        var values = new Dictionary<string,string> {
            ["grant_type"]="authorization_code",["code"]=code,["redirect_uri"]=Redirect(provider),
            ["client_id"]=cfg["clientId"]!.GetValue<string>(),["client_secret"]=await vault.GetAsync(owner,provider+".clientSecret",ct) ?? "",
            ["code_verifier"]=vault.Decrypt(rows[0]["verifier"]!.GetValue<string>(),owner+":pkce")
        };
        var tokens = await Exchange(provider,values,ct);
        await vault.PutAsync(owner,provider+".tokens",tokens.ToJsonString(),ct);
    }
    private async Task<JsonObject> Exchange(string provider,Dictionary<string,string> values,CancellationToken ct)
    {
        using var client=clients.CreateClient("provider");
        using var response=await client.PostAsync(Endpoints(provider).Token,new FormUrlEncodedContent(values),ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException("OAuth-Tokenaustausch fehlgeschlagen. Konto erneut verbinden.",502);
        var tokens=(await response.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        tokens["expiresAt"]=DateTimeOffset.UtcNow.AddSeconds(tokens["expires_in"]?.GetValue<int>() ?? 3600).ToString("O");
        return tokens;
    }
    public async Task<string> AccessAsync(string owner,string provider,CancellationToken ct)
    {
        await settings.RequireAsync(owner,provider,ct);
        var gate=Gates.GetOrAdd(owner+":"+provider,_=>new(1,1)); await gate.WaitAsync(ct);
        try {
            var raw=await vault.GetAsync(owner,provider+".tokens",ct) ?? throw new JarvisException("Konto noch nicht verbunden.",409);
            var tokens=JsonNode.Parse(raw)!.AsObject();
            if (DateTimeOffset.Parse(tokens["expiresAt"]!.GetValue<string>()) < DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var cfg=await settings.GetAsync(owner,provider,ct);
                var refreshed=await Exchange(provider,new() {
                    ["grant_type"]="refresh_token",["refresh_token"]=tokens["refresh_token"]?.GetValue<string>() ?? throw new JarvisException("Konto erneut verbinden.",409),
                    ["client_id"]=cfg["clientId"]!.GetValue<string>(),["client_secret"]=await vault.GetAsync(owner,provider+".clientSecret",ct) ?? ""
                },ct);
                if (refreshed["refresh_token"] is null) refreshed["refresh_token"]=tokens["refresh_token"]!.DeepClone();
                tokens=refreshed; await vault.PutAsync(owner,provider+".tokens",tokens.ToJsonString(),ct);
            }
            return tokens["access_token"]!.GetValue<string>();
        } finally { gate.Release(); }
    }
    public async Task<JsonNode?> RequestAsync(string owner,string provider,HttpMethod method,string relative,JsonNode? body,CancellationToken ct)
    {
        using var client=clients.CreateClient("provider");
        client.BaseAddress=new Uri(provider=="google" ? "https://www.googleapis.com/" : "https://graph.microsoft.com/v1.0/");
        using var request=new HttpRequestMessage(method,relative);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await AccessAsync(owner,provider,ct));
        if(body is not null) request.Content=JsonContent.Create(body);
        using var response=await client.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode) throw new JarvisException($"Integration meldet HTTP {(int)response.StatusCode}.",502);
        var text=await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject{["ok"]=true} : JsonNode.Parse(text);
    }
}
