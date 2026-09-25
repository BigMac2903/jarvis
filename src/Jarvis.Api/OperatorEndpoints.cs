using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Jarvis.Agent;

namespace Jarvis.Api;

public static class OperatorEndpoints
{
    public record ImportRequest(string Text, string Format);
    public static void MapOperator(this WebApplication app)
    {
        app.MapPost("/api/v1/operator/{kind}",async(string kind,JsonObject body,HttpContext ctx,Database db,CancellationToken ct)=>{
            if(kind is not ("tasks" or "goals"))throw new JarvisException("Ungültiger Aufgabentyp.");
            var description=body["description"]?.GetValue<string>()??"";
            if(description.Length is <3 or >10000)throw new JarvisException("Beschreibung benötigt 3–10.000 Zeichen.");
            var id=Guid.NewGuid().ToString("N");
            var data=new JsonObject{["title"]=body["title"]?.GetValue<string>()??description[..Math.Min(description.Length,100)],["description"]=description,
                ["status"]="Queued",["priority"]=Math.Clamp(body["priority"]?.GetValue<int>()??0,0,100),["progress"]=0,["created_at"]=DateTimeOffset.UtcNow.ToString("O")};
            if(body["deadline"] is JsonNode deadline){if(!DateTimeOffset.TryParse(deadline.GetValue<string>(),out var parsed))throw new JarvisException("Deadline ungültig.");data["deadline"]=parsed.ToString("O");}
            await db.PutAsync(ctx.Owner(),kind,id,data,ct);return new{id,status="Queued"};
        });
        app.MapPost("/api/v1/operator/tasks/{id}/{action}",async(string id,string action,HttpContext ctx,Database db,TaskEngine engine,CancellationToken ct)=>{
            var owner=ctx.Owner();var doc=await db.GetAsync(owner,"tasks",id,ct)??throw new JarvisException("Aufgabe unbekannt.",404);
            if(action=="stop"){
                engine.Cancel(owner,id);
                await db.ExecuteAsync("UPDATE approvals SET state='denied' WHERE owner=$1 AND task_id=$2 AND state='pending'",ct,owner,id);
                await db.ExecuteAsync("UPDATE documents SET data=jsonb_set(data,'{status}','\"Waiting\"') WHERE owner=$1 AND kind='tasks' AND id=$2 AND data->>'status'<>'Running'",ct,owner,id);
            }else if(action=="resume"){
                if(doc.Data["status"]?.GetValue<string>() is not ("Waiting" or "Failed"))throw new JarvisException("Aufgabe ist nicht fortsetzbar.",409);
                doc.Data["status"]="Queued";doc.Data.Remove("error");await db.PutAsync(owner,"tasks",id,doc.Data,ct);
            }else throw new JarvisException("Ungültige Aufgabenaktion.");
            return Results.Ok();
        });
        app.MapGet("/api/v1/memory/personal", (HttpContext ctx, PersonalMemory memory, CancellationToken ct) => memory.ListAsync(ctx.Owner(), true, ct));
        app.MapPut("/api/v1/memory/personal/{id}", async (string id, JsonObject body, HttpContext ctx, PersonalMemory memory, IAuthorizationStore audit, CancellationToken ct) => {
            await memory.SaveAsync(ctx.Owner(), id, body, ct); await audit.AuditAsync(ctx.Owner(), "Memory.Edit", "success", 0, null, new() { ["id"] = id }, ct); return Results.Ok();
        });
        app.MapPost("/api/v1/memory/import/preview", (ImportRequest request, HttpContext ctx, PersonalMemory memory, CancellationToken ct) => memory.PreviewAsync(ctx.Owner(), request.Text, request.Format, ct));
        app.MapGet("/api/v1/memory/personal/{id}/history", async (string id, HttpContext ctx, Database db, PersonalMemory memory, CancellationToken ct) => {
            var owner = ctx.Owner(); return (await db.ListAsync(owner, "memory-history", 1000, ct)).Where(d => d.Data["memoryId"]?.GetValue<string>() == id)
                .Select(d => new { d.Id, at = d.Data["at"], data = memory.Reveal(owner, id, d.Data) });
        });
        app.MapDelete("/api/v1/memory/personal/{id}", async (string id, HttpContext ctx, Database db, CancellationToken ct) => {
            await db.DeleteAsync(ctx.Owner(), "personal-memory", id, ct);
            await db.ExecuteAsync("DELETE FROM documents WHERE owner=$1 AND kind='memory-history' AND data->>'memoryId'=$2", ct, ctx.Owner(), id); return Results.Ok();
        });
        app.MapGet("/api/v1/ai/usage", async (HttpContext ctx, Database db, CancellationToken ct) => await db.QueryAsync(
            "SELECT model,task_type,agent,connector,date_trunc('day',created_at AT TIME ZONE 'UTC') AS day,sum(input_tokens) AS input_tokens,sum(cached_tokens) AS cached_tokens,sum(output_tokens) AS output_tokens,sum(reasoning_tokens) AS reasoning_tokens,sum(estimated_cost) AS estimated_cost,bool_and(price_known) AS price_known,count(*) AS calls FROM ai_usage WHERE owner=$1 AND created_at>now()-interval '31 days' GROUP BY model,task_type,agent,connector,day ORDER BY day DESC", ct, ctx.Owner()));
        app.MapPost("/api/v1/ai/models/discover", async (HttpContext ctx, AiClient ai, Database db, CancellationToken ct) => {
            var (client, _) = await ai.ClientAsync(ctx.Owner(), ct); using var dispose = client;
            var result = await client.GetFromJsonAsync<JsonObject>("models", ct) ?? new(); var count = 0;
            foreach (var model in result["data"]?.AsArray() ?? []) {
                var name = model?["id"]?.GetValue<string>(); if (name is null || name.Length > 150) continue;
                var id = Crypto.Hash(name);
                if (await db.GetAsync(ctx.Owner(), "ai-models", id, ct) is not null) continue;
                await db.PutAsync(ctx.Owner(), "ai-models", id, new() { ["model_name"] = name, ["provider"] = "configured", ["enabled"] = false, ["validated"] = false, ["status"] = "AVAILABLE_NOT_VALIDATED" }, ct); count++;
            }
            return new { discovered = count, note = "Preise und Fähigkeiten werden nicht aus Modellnamen erfunden; vor Verwendung prüfen und validieren." };
        });
    }
}
