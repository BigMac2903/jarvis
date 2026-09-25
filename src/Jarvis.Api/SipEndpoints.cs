using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;

namespace Jarvis.Api;

public static class SipEndpoints
{
    public record IncomingCall(string Owner, string AccountId, string Number);
    public record SipEvent(string Owner, string CallId, string State);
    public static void MapSip(this WebApplication app)
    {
        app.MapGet("/internal/sip/config", async (Database db, Settings settings, Vault vault, CancellationToken ct) => {
            var result = new List<SipAccount>();
            foreach (var user in await db.QueryAsync("SELECT id FROM users", ct))
            {
                var owner = user["id"]!.GetValue<string>();
                var cfg = await settings.GetAsync(owner, "sip", ct);
                if (cfg["enabled"]?.GetValue<bool>() != true) continue;
                var accounts = cfg["accounts"]?.Deserialize<List<SipAccount>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                foreach (var account in accounts.Where(a => a.Enabled))
                    result.Add(account with { Owner = owner, Password = await vault.GetAsync(owner, "sip.account." + account.Id, ct) ?? "" });
            }
            return result;
        });
        app.MapGet("/api/v1/phone/sip/status", (HttpContext ctx, SipPhoneProvider sip, CancellationToken ct) => sip.HealthAsync(ctx.Owner(), ct));
        app.MapPost("/internal/sip/incoming", async (IncomingCall request, Database db, Settings settings, CancellationToken ct) => {
            await settings.RequireAsync(request.Owner, "sip", ct);
            await settings.RequireAsync(request.Owner, "ai", ct);
            await PhoneTools.RequireAudioAsync(settings, request.Owner, "sip", ct);
            var cfg = await settings.GetAsync(request.Owner, "sip", ct);
            var account = cfg["accounts"]?.Deserialize<List<SipAccount>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?
                .SingleOrDefault(a => a.Id == request.AccountId && a.Enabled && a.AllowedDirections.Contains("inbound"))
                ?? throw new JarvisException("Konto nicht freigegeben.", 403);
            var known = (await db.ListAsync(request.Owner, "contacts", 1000, ct)).Any(c =>
                new[] { "phone", "mobile", "landline", "preferredNumber" }.Any(k => c.Data[k]?.GetValue<string>() == request.Number));
            var allowed = account.IncomingMode switch {
                "KNOWN_CONTACTS_ONLY" => known,
                "ALLOWLIST" => account.IncomingAllowlist.Contains(request.Number),
                "ALL_CALLERS" or "UNKNOWN_CALLERS_TO_SCREENING" => true,
                _ => false
            };
            if (!allowed) throw new JarvisException("Eingehende Anrufe nicht freigegeben.", 403);
            if (request.Number.Length > 80 || request.Number.Any(char.IsControl)) throw new JarvisException("Ungültiger Anrufer.");
            var phone = await settings.GetAsync(request.Owner, "phone", ct);
            var recent = await db.QueryAsync("SELECT count(*) AS count FROM documents WHERE owner=$1 AND kind='calls' AND data->>'direction'='inbound' AND updated_at>now()-interval '1 hour'", ct, request.Owner);
            if (recent[0]["count"]!.GetValue<long>() >= Math.Clamp(phone["maxIncomingPerHour"]?.GetValue<int>() ?? 10, 1, 60)) throw new JarvisException("Anruflimit erreicht.", 429);
            var id = Guid.NewGuid().ToString("N");
            var duration = PhonePolicy.Duration(new(), phone);
            await db.PutAsync(request.Owner, "calls", id, new() {
                ["provider"] = "sip", ["direction"] = "inbound", ["sip_account"] = account.Id, ["number"] = request.Number,
                ["status"] = "Ringing", ["started_at"] = DateTimeOffset.UtcNow.ToString("O"), ["recording"] = false,
                ["purpose"] = "Nimm das Anliegen, Dringlichkeit und Rückrufwunsch auf. Gib keine privaten Informationen preis. Du kannst keine Identität anhand einer Rufnummer bestätigen und keine verbindliche Zusage machen.",
                ["screening"] = !known, ["max_duration"] = duration, ["transcript"] = new JsonArray()
            }, ct);
            return new { request.Owner, callId = id, request.AccountId, request.Number, maxDuration = duration };
        });
        app.MapPost("/internal/sip/events", async (SipEvent e, Database db, IEventSink events, CancellationToken ct) => {
            if (e.State is not ("Calling" or "Ringing" or "Connected" or "OnHold" or "Transferring" or "Completed" or "Failed")) throw new JarvisException("Ungültiger Anrufstatus.");
            var call = await db.GetAsync(e.Owner, "calls", e.CallId, ct) ?? throw new JarvisException("Anruf unbekannt.", 404);
            if (call.Data["provider"]?.GetValue<string>() != "sip") throw new JarvisException("Providerkonflikt.", 403);
            var patch = new JsonObject { ["status"] = e.State };
            if (e.State == "Connected" && call.Data["answered_at"] is null) patch["answered_at"] = DateTimeOffset.UtcNow.ToString("O");
            if (e.State is "Completed" or "Failed") {
                patch["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
                patch["result"] = e.State;
                patch["duration"] = DateTimeOffset.TryParse(call.Data["answered_at"]?.GetValue<string>(), out var answered) ? Math.Max(0, (int)(DateTimeOffset.UtcNow - answered).TotalSeconds) : 0;
            }
            await db.ExecuteAsync("UPDATE documents SET data=data || $3::jsonb,updated_at=now() WHERE owner=$1 AND kind='calls' AND id=$2", ct, e.Owner, e.CallId, patch.ToJsonString());
            await events.SendAsync(e.Owner, "call", new { callId = e.CallId, status = e.State }, ct);
            return new { ok = true };
        });
        app.Map("/internal/sip/media/{callId}", SipAudioBridge.HandleAsync);
    }
}
