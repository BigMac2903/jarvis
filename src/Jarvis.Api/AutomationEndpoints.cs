using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;
namespace Jarvis.Api;
public static class AutomationEndpoints
{
    public static void MapAutomationHooks(this WebApplication app)
    {
        app.MapPost("/api/v1/automation-hooks",async(JsonObject input,HttpContext ctx,Database db,IConfiguration cfg,CancellationToken ct)=>{
            var name=input["event"]?.GetValue<string>()??"";
            if(name.Length is <1 or >100)throw new JarvisException("Ereignisname fehlt.");
            var token=Crypto.Token();await db.PutAsync(ctx.Owner(),"automation-hooks",Crypto.Hash(token),new(){["event"]=name},ct);
            return new{url=cfg["PUBLIC_URL"]!.TrimEnd('/')+"/api/v1/automation-webhooks/"+token};
        });
        app.MapPost("/api/v1/automation-webhooks/{token}",async(string token,Database db,CancellationToken ct)=>{
            if(token.Length!=64)throw new JarvisException("Webhook ungültig.",403);
            var hooks=await db.QueryAsync("SELECT owner,data::text FROM documents WHERE kind='automation-hooks' AND id=$1",ct,Crypto.Hash(token));
            if(hooks.Count!=1)throw new JarvisException("Webhook ungültig.",403);
            var owner=hooks[0]["owner"]!.GetValue<string>();var hook=JsonNode.Parse(hooks[0]["data"]!.GetValue<string>())!;
            await db.PutAsync(owner,"device-events",Guid.NewGuid().ToString("N"),new(){["name"]=hook["event"]!.DeepClone(),["receivedAt"]=DateTimeOffset.UtcNow.ToString("O")},ct);
            return Results.Accepted();
        }).RequireRateLimiting("auth");
    }
}
