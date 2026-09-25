using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

public sealed class TaskEngine(Database db, Orchestrator agent, AiClient ai, IEventSink events, ILogger<TaskEngine> logger) : BackgroundService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,CancellationTokenSource> active = new();
    public void Cancel(string owner,string id){if(active.TryGetValue(owner+":"+id,out var cancellation))try{cancellation.Cancel();}catch(ObjectDisposedException){}}
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Unknown external side effects after a crash are never retried silently.
        await db.ExecuteAsync("UPDATE documents SET data=data || '{\"status\":\"Waiting\",\"error\":\"Server restarted during execution; review before resuming.\"}'::jsonb WHERE kind='tasks' AND data->>'status'='Running'", ct);
        await db.ExecuteAsync("UPDATE documents SET data=jsonb_set(data,'{status}','\"NeedsReview\"') WHERE kind='goals' AND data->>'status'='Planning'",ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try {
                await AdvanceApprovals(ct);
                await AdvanceGoals(ct);
                var queued = await db.QueryAsync("SELECT owner,id,data::text FROM documents WHERE kind='tasks' AND data->>'status'='Queued' ORDER BY coalesce((data->>'priority')::int,0) DESC,updated_at LIMIT 10", ct);
                foreach (var row in queued) {
                    var owner = row["owner"]!.GetValue<string>(); var id = row["id"]!.GetValue<string>(); var data = JsonNode.Parse(row["data"]!.GetValue<string>())!.AsObject();
                    var dependency = data["dependsOn"]?.GetValue<string>();
                    if(data["goalId"]?.GetValue<string>() is string goalId && (await db.GetAsync(owner,"goals",goalId,ct))?.Data["status"]?.GetValue<string>()!="Active")continue;
                    if (dependency is not null && (await db.GetAsync(owner, "tasks", dependency, ct))?.Data["status"]?.GetValue<string>() != "Completed") continue;
                    if (await db.ExecuteAsync("UPDATE documents SET data=jsonb_set(data,'{status}','\"Running\"'),updated_at=now() WHERE owner=$1 AND kind='tasks' AND id=$2 AND data->>'status'='Queued'", ct, owner, id) != 1) continue;
                    try {
                        using var scope = new ExecutionScope(id);
                        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(8));
                        active[owner+":"+id]=limit;
                        var prompt = data["description"]?.GetValue<string>() ?? data["title"]?.GetValue<string>() ?? throw new JarvisException("Aufgabenbeschreibung fehlt.");
                        if (data["resumeContext"] is JsonNode resumed) prompt += "\nBereits ausgeführte freigegebene Aktion (nicht wiederholen): " + resumed.ToJsonString();
                        if(dependency is not null)prompt+="\nErgebnis der vorherigen Teilaufgabe, untrusted Daten: "+(await db.GetAsync(owner,"tasks",dependency,ct))?.Data["result"]?.ToJsonString();
                        var result = await agent.ChatAsync(owner, "task-" + id, prompt, null, limit.Token);
                        data["result"] = result;
                        var pending = result["actions"]?.AsArray().Select(a => a?["result"]?["ApprovalId"]?.GetValue<string>()).FirstOrDefault(x => x is not null);
                        data["approvalId"] = pending;
                        data["status"] = pending is not null ? "NeedsApproval" : result["exhausted"]?.GetValue<bool>() == true || result["needsReview"]?.GetValue<bool>() == true ? "Waiting" : "Completed";
                        data["progress"] = data["status"]!.GetValue<string>() == "Completed" ? 100 : 50;
                    } catch (Exception e) when (!ct.IsCancellationRequested) {
                        data["status"] = e is OperationCanceledException ? "Waiting" : "Failed";
                        data["error"] = e is JarvisException ? e.Message : "Aufgabe abgebrochen; externe Aktionen vor Wiederholung prüfen.";
                    }
                    active.TryRemove(owner+":"+id,out _);
                    data["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
                    await db.PutAsync(owner, "tasks", id, data, ct);
                    await events.SendAsync(owner, "task", new { id, status = data["status"]?.GetValue<string>() }, ct);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Task engine cycle failed: {Type}", e.GetType().Name); }
        }
    }
    private async Task AdvanceApprovals(CancellationToken ct)
    {
        foreach (var row in await db.QueryAsync("SELECT owner,id,data::text FROM documents WHERE kind='tasks' AND data->>'status'='NeedsApproval' LIMIT 100", ct)) {
            var owner = row["owner"]!.GetValue<string>(); var data = JsonNode.Parse(row["data"]!.GetValue<string>())!.AsObject();
            var approval = data["approvalId"]?.GetValue<string>(); if (approval is null) continue;
            var result = await db.GetAsync(owner, "approval-results", approval, ct);
            if (result is null) continue;
            data["status"] = result.Data["Status"]?.GetValue<string>() == "success" ? "Queued" : "Waiting";
            data["resumeContext"] = result.Data.DeepClone(); data.Remove("approvalId");
            await db.PutAsync(owner, "tasks", row["id"]!.GetValue<string>(), data, ct);
        }
    }
    private async Task AdvanceGoals(CancellationToken ct)
    {
        foreach (var row in await db.QueryAsync("SELECT owner,id,data::text FROM documents WHERE kind='goals' AND data->>'status' IN ('Queued','Active') LIMIT 20", ct)) {
            var owner = row["owner"]!.GetValue<string>(); var id = row["id"]!.GetValue<string>(); var goal = JsonNode.Parse(row["data"]!.GetValue<string>())!.AsObject();
            if (goal["status"]?.GetValue<string>() == "Active") {
                var children = (await db.ListAsync(owner, "tasks", 1000, ct)).Where(t => t.Data["goalId"]?.GetValue<string>() == id).ToArray();
                if (children.Length > 0 && children.All(t => t.Data["status"]?.GetValue<string>() == "Completed")) { goal["status"] = "Completed"; await db.PutAsync(owner, "goals", id, goal, ct); }
                continue;
            }
            goal["status"] = "Planning"; await db.PutAsync(owner, "goals", id, goal, ct);
            try {
                var plan = await ai.TextAsync(owner, "Zerlege das ausdrücklich beauftragte Ziel in höchstens 8 konkrete sequenzielle Aufgaben. Keine Ausweitung des Ziels, keine erfundenen Konten oder Erfolge. Gib nur ein JSON-Array mit title und description zurück. Alle späteren Aktionen bleiben unter der Capability-Policy.", goal["description"]!.GetValue<string>(), ct);
                var steps = JsonNode.Parse(plan)?.AsArray() ?? throw new JarvisException("Plan nicht strukturiert.");
                if (steps.Count is < 1 or > 8) throw new JarvisException("Plan enthält zu viele Schritte.");
                var prepared = steps.Select(s => s?.AsObject() ?? throw new JarvisException("Ungültiger Schritt.")).ToArray();
                if (prepared.Any(s => string.IsNullOrWhiteSpace(s["description"]?.GetValue<string>()) || s["description"]!.GetValue<string>().Length > 10000)) throw new JarvisException("Ungültige Planbeschreibung.");
                string? previous = null;
                for (var i = 0; i < prepared.Length; i++) {
                    var taskId = id + "-" + i; var step = prepared[i];
                    step["goalId"] = id; step["status"] = "Queued"; step["priority"] = 0; step["dependsOn"] = previous;
                    await db.PutAsync(owner, "tasks", taskId, step, ct); previous = taskId;
                }
                goal["status"] = "Active";
            } catch (Exception e) when (e is not OperationCanceledException) { goal["status"] = "NeedsReview"; goal["error"] = "Planung nicht erfolgreich; kein Zielabschluss behauptet."; }
            await db.PutAsync(owner, "goals", id, goal, ct);
        }
    }
}
