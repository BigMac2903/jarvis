using System.Text.Json.Nodes;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
namespace Jarvis.Api;
public static class AdminEndpoints
{
    public record Decision(bool Approve);
    public static void MapAdministration(this WebApplication app)
    {
        app.MapGet("/api/v1/settings",async(HttpContext ctx,Settings settings,CancellationToken ct)=>{
            var result=new JsonObject();foreach(var section in Settings.Sections)result[section]=await settings.GetAsync(ctx.Owner(),section,ct);return result;
        });
        app.MapPut("/api/v1/settings/{section}",async(string section,JsonObject data,HttpContext ctx,Settings settings,IAuthorizationStore audit,CancellationToken ct)=>{
            await settings.SaveAsync(ctx.Owner(),section,data,ct);
            await audit.AuditAsync(ctx.Owner(),"Settings.Update","success",0,null,new(){["section"]=section},ct);return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/v1/settings/{section}/test",async(string section,HttpContext ctx,AiClient ai,OAuthService oauth,PhoneService phone,Settings settings,Vault vault,IHttpClientFactory clients,CancellationToken ct)=>{
            switch(section){
                case "ai":await ai.TestAsync(ctx.Owner(),ct);break;
                case "google":await oauth.RequestAsync(ctx.Owner(),section,HttpMethod.Get,"calendar/v3/calendars/primary/events?maxResults=1",null,ct);break;
                case "microsoft":await oauth.RequestAsync(ctx.Owner(),section,HttpMethod.Get,"me",null,ct);break;
                case "twilio":await phone.TestAsync(ctx.Owner(),ct);break;
                case "internet":await new SearchProvider(clients,settings,vault,ctx.Owner()).SearchAsync("IETF HTTP",1,ct);break;
                default:throw new JarvisException("Für diesen Bereich ist kein Kontotest erforderlich.");
            }return new{ok=true};
        });
        app.MapPost("/api/v1/oauth/{provider}/start",async(string provider,HttpContext ctx,OAuthService oauth,CancellationToken ct)=>new{url=await oauth.StartAsync(ctx.Owner(),provider,ct)});
        app.MapGet("/api/v1/oauth/{provider}/callback",async(string provider,string state,string code,OAuthService oauth,CancellationToken ct)=>{
            await oauth.CompleteAsync(provider,state,code,ct);return Results.Redirect("/?connected="+Uri.EscapeDataString(provider));
        });
        app.MapGet("/api/v1/tools",(ToolDispatcher tools)=>tools.Definitions);
        app.MapGet("/api/v1/permissions",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.ListAsync(ctx.Owner(),"permissions",1000,ct));
        app.MapPut("/api/v1/permissions/{tool}",async(string tool,JsonObject data,HttpContext ctx,Database db,ToolDispatcher dispatcher,IAuthorizationStore audit,CancellationToken ct)=>{
            var definition=dispatcher.Definitions.FirstOrDefault(x=>x.Name==tool)??throw new JarvisException("Tool unbekannt.");
            if(!Enum.TryParse<Permission>(data["permission"]?.GetValue<string>(),out var permission))throw new JarvisException("Ungültige Berechtigung.");
            if(definition.Risk==Risk.AlwaysConfirm&&permission==Permission.Allow)throw new JarvisException("Dieses Tool benötigt immer eine Bestätigung.");
            var saved=new JsonObject{["permission"]=permission.ToString()};
            if(data["tenMinutes"]?.GetValue<bool>()==true)saved["expiresAt"]=DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
            await db.PutAsync(ctx.Owner(),"permissions",tool,saved,ct);await audit.AuditAsync(ctx.Owner(),"Permissions.Update","success",0,null,new(){["tool"]=tool},ct);return Results.Ok();
        });
        app.MapGet("/api/v1/approvals",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.QueryAsync("SELECT id,tool,args::text,state,expires_at,created_at FROM approvals WHERE owner=$1 AND state='pending' AND expires_at>now() ORDER BY created_at DESC",ct,ctx.Owner()));
        app.MapPost("/api/v1/approvals/{id}",async(string id,Decision decision,HttpContext ctx,Database db,ToolDispatcher tools,CancellationToken ct)=>{
            var rows=await db.QueryAsync("UPDATE approvals SET state=$1 WHERE id=$2 AND owner=$3 AND state='pending' AND expires_at>now() RETURNING tool,args::text",ct,decision.Approve?"approved":"denied",id,ctx.Owner());
            if(rows.Count!=1)throw new JarvisException("Freigabe abgelaufen oder bereits bearbeitet.",409);
            if(!decision.Approve)return new ToolResult("denied");
            return await tools.ExecuteAsync(new(ctx.Owner()),rows[0]["tool"]!.GetValue<string>(),JsonNode.Parse(rows[0]["args"]!.GetValue<string>())!.AsObject(),id,ct);
        });
        app.MapGet("/api/v1/audit",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.QueryAsync("SELECT id,tool,status,duration_ms,approval,parameters::text,created_at FROM audit WHERE owner=$1 ORDER BY id DESC LIMIT 300",ct,ctx.Owner()));
        app.MapGet("/api/v1/data/{kind}",async(string kind,HttpContext ctx,Database db,CancellationToken ct)=>{Allowed(kind);return await db.ListAsync(ctx.Owner(),kind,500,ct);});
        app.MapPut("/api/v1/data/{kind}/{id}",async(string kind,string id,JsonObject data,HttpContext ctx,Database db,ToolDispatcher tools,CancellationToken ct)=>{
            if(kind is not ("contacts" or "automations" or "tasks"))throw new JarvisException("Schreiben nicht erlaubt.",403);
            if(id.Length>100||data.ToJsonString().Length>50000)throw new JarvisException("Datensatz zu groß.");
            if(kind=="automations"){
                var tool=tools.Definitions.FirstOrDefault(t=>t.Name==data["tool"]?.GetValue<string>())??throw new JarvisException("Tool unbekannt.");
                ToolDispatcher.Validate(tool,data["args"]?.AsObject()??new());
                if(data["trigger"]?.GetValue<string>() is not ("time" or "cron" or "location" or "device" or "webhook" or "calendar"))throw new JarvisException("Trigger ungültig.");
                if(data["trigger"]?.GetValue<string>()=="cron")data["nextRun"]=Jarvis.Agent.AutomationWorker.Next(data,DateTimeOffset.UtcNow).ToString("O");
                if(data["trigger"]?.GetValue<string>() is "location" or "device" or "webhook")data["lastEventAt"]=DateTimeOffset.UtcNow.ToString("O");
            }
            await db.PutAsync(ctx.Owner(),kind,id,data,ct);return Results.Ok();
        });
        app.MapDelete("/api/v1/data/{kind}/{id}",async(string kind,string id,HttpContext ctx,Database db,CancellationToken ct)=>{
            Allowed(kind);
            if(kind=="research"){
                var active=await db.QueryAsync("SELECT id FROM documents WHERE owner=$1 AND kind='jobs' AND ($2='all' OR id=$2) AND data->>'status' IN ('running','pending') LIMIT 1",ct,ctx.Owner(),id);
                if(active.Count>0)throw new JarvisException("Laufende Recherche zuerst stoppen und auf den Abbruch warten.",409);
                await db.DeleteAsync(ctx.Owner(),"jobs",id=="all"?null:id,ct);
                await db.DeleteAsync(ctx.Owner(),"research-cache",null,ct);
            }
            await db.DeleteAsync(ctx.Owner(),kind,id=="all"?null:id,ct);return Results.Ok();
        });
        app.MapPut("/api/v1/browser/credentials/{id}",async(string id,JsonObject data,HttpContext ctx,Vault vault,CancellationToken ct)=>{
            if(id.Length>100||data.ToJsonString().Length>20000)throw new JarvisException("Ungültige Credentials.");
            foreach(var key in new[]{"host","username","password","usernameSelector","passwordSelector"})if(string.IsNullOrWhiteSpace(data[key]?.GetValue<string>()))throw new JarvisException("Feld fehlt: "+key);
            await vault.PutAsync(ctx.Owner(),"browser."+id,data.ToJsonString(),ct);return Results.Ok();
        });
    }
    private static void Allowed(string kind){if(kind is not ("contacts" or "automations" or "tasks" or "memory" or "research" or "chats" or "calls" or "notifications"))throw new JarvisException("Bereich nicht freigegeben.",403);}
}
