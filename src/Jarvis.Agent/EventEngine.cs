using System.Text.Json.Nodes;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

public record EventDecision(string Priority, string Action, bool Deferred);
public static class EventPolicy
{
    public static EventDecision Evaluate(JsonObject rule, JsonObject settings, int hour)
    {
        var score = Math.Clamp(rule["urgency"]?.GetValue<int>() ?? 0, 0, 100) * 0.4 + Math.Clamp(rule["impact"]?.GetValue<int>() ?? 0, 0, 100) * 0.4 + Math.Clamp(rule["relevance"]?.GetValue<int>() ?? 0, 0, 100) * 0.2;
        var priority = score >= 85 ? "CRITICAL" : score >= 65 ? "IMPORTANT" : score >= 30 ? "NORMAL" : "LOW";
        var start = settings["quietStart"]?.GetValue<int>() ?? 22; var end = settings["quietEnd"]?.GetValue<int>() ?? 6;
        var quiet = start != end && (start > end ? hour >= start || hour < end : hour >= start && hour < end);
        return new(priority, priority == "CRITICAL" && settings["criticalCalls"]?.GetValue<bool>() == true && rule["allowCall"]?.GetValue<bool>() == true ? "Call" : priority == "LOW" ? "Log" : "Notify", quiet && priority is "LOW" or "NORMAL");
    }
}
public sealed class EventEngine(Database db, Settings settings, ToolDispatcher tools, IEventSink sink, ILogger<EventEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct)) try {
            foreach (var user in await db.QueryAsync("SELECT id,timezone FROM users", ct)) {
                var owner = user["id"]!.GetValue<string>(); var cfg = await settings.GetAsync(owner, "events", ct);
                if (cfg["enabled"]?.GetValue<bool>() != true) continue;
                var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(user["timezone"]!.GetValue<string>())).Hour;
                var rules = await db.ListAsync(owner, "event-rules", 100, ct);
                foreach (var e in await db.ListAsync(owner, "device-events", 200, ct)) {
                    if (e.UpdatedAt < DateTimeOffset.UtcNow.AddDays(-1)) continue;
                    var handled = await db.GetAsync(owner, "events", e.Id, ct);
                    if (handled is not null && handled.Data["status"]?.GetValue<string>() != "Deferred") continue;
                    var rule = rules.FirstOrDefault(r => r.Data["enabled"]?.GetValue<bool>() == true && r.Data["event"]?.GetValue<string>() == e.Data["name"]?.GetValue<string>() &&
                        (r.Data["deviceId"] is null || r.Data["deviceId"]?.GetValue<string>() == e.Data["deviceId"]?.GetValue<string>()));
                    if (rule is null) continue;
                    var decision = EventPolicy.Evaluate(rule.Data, cfg, hour);
                    var status = new JsonObject { ["priority"] = decision.Priority, ["action"] = decision.Action, ["source"] = e.Data.DeepClone(), ["ruleId"] = rule.Id,
                        ["status"] = decision.Deferred ? "Deferred" : "Claimed" };
                    // A persisted claim prevents a restart from repeating a call/notification after an uncertain result.
                    await db.PutAsync(owner, "events", e.Id, status, ct);
                    if (decision.Deferred) continue;
                    var message = rule.Data["message"]?.GetValue<string>() ?? "Ein konfiguriertes Ereignis ist eingetreten.";
                    if (decision.Action == "Call") {
                        var result = await tools.ExecuteAsync(new(owner), "Phone.CallUser", new() { ["purpose"] = message, ["eventId"] = e.Id }, null, ct);
                        status["status"] = result.Status; status["approvalId"] = result.ApprovalId;
                    } else {
                        status["status"] = "Completed";
                        if (decision.Action == "Notify") await tools.ExecuteAsync(new(owner), "Notification.Send", new() { ["title"] = decision.Priority, ["message"] = message }, null, ct);
                    }
                    await db.PutAsync(owner, "events", e.Id, status, ct);
                    await sink.SendAsync(owner, "event", new { id = e.Id, priority = decision.Priority, action = decision.Action }, ct);
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (Exception e) { logger.LogWarning("Event cycle failed: {Type}", e.GetType().Name); }
    }
}
