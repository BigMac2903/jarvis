using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class PhoneTools(IEnumerable<IPhoneProvider> providers, Settings settings, Database db) : IToolHandler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Phone.Call", "Einen bestätigten Telefonauftrag ausführen. Standardprovider SIP.", Risk.Confirm, new() {
            ["number"] = new("string", "E.164-Nummer; alternativ contact_id", false, 20),
            ["contact_id"] = new("string", "Gespeicherter Kontakt", false, 100),
            ["purpose"] = new("string", "Konkreter Gesprächsauftrag", true, 5000),
            ["context"] = new("string", "Nur für diesen Anruf freigegebener Kontext", false, 5000),
            ["max_duration"] = new("integer", "Maximale Gesprächsdauer in Sekunden", false),
            ["priority"] = new("string", "Priorität", false, Choices: ["normal", "important", "critical"]),
            ["usage"] = new("string", "Kontoverwendung", false, Choices: ["private", "business", "internal"])
        }, 60),
        new("Phone.CallUser", "Den konfigurierten Benutzer wegen eines wichtigen Ereignisses anrufen.", Risk.Confirm,
            new() { ["purpose"] = new("string", "Anlass und konkrete Information", MaxLength: 5000), ["eventId"] = new("string", "Eindeutiges Ereignis zur Entdoppelung", MaxLength: 100) }, 60),
        new("Phone.Hangup", "Aktiven Anruf beenden.", Risk.Confirm, CallFields()),
        new("Phone.Hold", "Aktiven Anruf halten.", Risk.Confirm, CallFields()),
        new("Phone.Resume", "Gehaltenen Anruf fortsetzen.", Risk.Confirm, CallFields()),
        new("Phone.Transfer", "Anruf per SIP REFER zu einer erlaubten Nummer weiterleiten.", Risk.AlwaysConfirm,
            new() { ["callId"] = new("string", "Anruf-ID", MaxLength: 100), ["number"] = new("string", "E.164-Ziel", MaxLength: 20) }),
        new("Phone.SendDtmf", "DTMF senden. Keine sensiblen Geheimnisse als Toolargument eingeben.", Risk.Confirm,
            new() { ["callId"] = new("string", "Anruf-ID", MaxLength: 100), ["digits"] = new("string", "0–9, Stern und Raute", MaxLength: 30) }),
        new("Phone.Status", "Registrierung und Verfügbarkeit des konfigurierten Telefonieproviders prüfen.", Risk.Safe, new())
    ];
    private static Dictionary<string, Field> CallFields() => new() { ["callId"] = new("string", "Anruf-ID", MaxLength: 100) };
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct)
    {
        var owner = actor.UserId;
        var cfg = await settings.GetAsync(owner, "phone", ct);
        var providerName = cfg["provider"]?.GetValue<string>() ?? "sip";
        IPhoneProvider Provider(string selected) => providers.FirstOrDefault(p => p.Name == selected) ?? throw new JarvisException("Telefonieprovider unbekannt.");
        if (name == "Phone.Status") return await Provider(providerName).HealthAsync(owner, ct);
        if (name is not ("Phone.Call" or "Phone.CallUser"))
        {
            var id = args["callId"]!.GetValue<string>();
            var call = await db.GetAsync(owner, "calls", id, ct) ?? throw new JarvisException("Anruf nicht gefunden.", 404);
            if (name == "Phone.Transfer") PhonePolicy.Number(args["number"]!.GetValue<string>(), cfg);
            return await Provider(call.Data["provider"]?.GetValue<string>() ?? "twilio").ControlAsync(owner, id, name[6..], args, ct);
        }
        await settings.RequireAsync(owner, "ai", ct);
        await RequireAudioAsync(settings, owner, providerName, ct);
        var request = args.DeepClone().AsObject();
        if (name == "Phone.CallUser") request["number"] = cfg["userNumber"]?.DeepClone();
        else if (request["contact_id"] is JsonNode contactId)
        {
            var contact = await db.GetAsync(owner, "contacts", contactId.GetValue<string>(), ct) ?? throw new JarvisException("Kontakt unbekannt.");
            request["number"] ??= contact.Data["preferredNumber"]?.DeepClone() ?? contact.Data["phone"]?.DeepClone();
            request["contactGroup"] = contact.Data["group"]?.DeepClone();
        }
        var number = PhonePolicy.Number(request["number"]?.GetValue<string>() ?? "", cfg);
        request["number"] = number;
        request["max_duration"] = PhonePolicy.Duration(request, cfg);
        request["objective"] = request["purpose"]?.DeepClone();
        var user = (await db.QueryAsync("SELECT timezone FROM users WHERE id=$1", ct, owner)).Single();
        request["localHour"] = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(user["timezone"]!.GetValue<string>())).Hour;
        var gate = Gates.GetOrAdd(owner, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var count = await db.QueryAsync("SELECT count(*) AS count FROM documents WHERE owner=$1 AND kind='phone-attempts' AND updated_at>now()-interval '1 hour'", ct, owner);
            if (count[0]["count"]!.GetValue<long>() >= Math.Clamp(cfg["maxCallsPerHour"]?.GetValue<int>() ?? 3, 1, 20))
                throw new JarvisException("Stündliches Anruflimit erreicht.", 429);
            var eventId = request["eventId"]?.GetValue<string>();
            if (eventId is not null && await db.GetAsync(owner, "phone-event-calls", eventId, ct) is not null)
                throw new JarvisException("Für dieses Ereignis wurde bereits ein Anruf versucht.", 409);
            var callId = Guid.NewGuid().ToString("N"); request["callId"] = callId;
            await db.PutAsync(owner, "phone-attempts", callId, new() { ["provider"] = providerName }, ct);
            if (eventId is not null) await db.PutAsync(owner, "phone-event-calls", eventId, new() { ["callId"] = callId }, ct);
            return await Provider(providerName).StartAsync(owner, request, ct);
        }
        finally { gate.Release(); }
    }
    public static async Task RequireAudioAsync(Settings settings, string owner, string provider, CancellationToken ct)
    {
        if (provider != "sip") return;
        await ModelRouter.GuardRealtimeBudgetAsync(settings,owner,ct);
        var phone = await settings.GetAsync(owner, "phone", ct);
        var ai = await settings.GetAsync(owner, "ai", ct);
        if (phone["audioEnabled"]?.GetValue<bool>() != true || ai["baseUrl"]?.GetValue<string>()?.TrimEnd('/') != "https://api.openai.com/v1" ||
            string.IsNullOrWhiteSpace(ai["realtimeModel"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(ai["transcriptionModel"]?.GetValue<string>()))
            throw new JarvisException("Vor SIP-Anrufen Audioübertragung aktivieren und OpenAI-Realtime-/Transkriptionsmodelle konfigurieren.", 409);
    }
}
