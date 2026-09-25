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
        app.MapPost("/api/v1/settings/{section}/test",async(string section,HttpContext ctx,AiClient ai,OAuthService oauth,PhoneService phone,SipPhoneProvider sip,NextcloudTools nextcloud,ImmichTools immich,Settings settings,Vault vault,IHttpClientFactory clients,CancellationToken ct)=>{
            switch(section){
                case "ai":await ai.TestAsync(ctx.Owner(),ct);break;
                case "google":await oauth.RequestAsync(ctx.Owner(),section,HttpMethod.Get,"calendar/v3/calendars/primary/events?maxResults=1",null,ct);break;
                case "microsoft":await oauth.RequestAsync(ctx.Owner(),section,HttpMethod.Get,"me",null,ct);break;
                case "twilio":await phone.TestAsync(ctx.Owner(),ct);break;
                case "sip":var health=await sip.HealthAsync(ctx.Owner(),ct);if(health?["accounts"]?.AsArray().Any(a=>a?["registered"]?.GetValue<bool>()==true)!=true)throw new JarvisException("SIP-Dienst erreichbar, aber kein Konto registriert.",409);break;
                case "nextcloud":await nextcloud.List(ctx.Owner(),"/",ct);break;
                case "immich":await immich.TestAsync(ctx.Owner(),ct);break;
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
            if(!Enum.TryParse<Permission>(data["permission"]?.GetValue<string>(),out var permission)||!Enum.IsDefined(permission))throw new JarvisException("Ungültige Berechtigung.");
            if(definition.Risk==Risk.AlwaysConfirm&&permission is Permission.Allow or Permission.Auto or Permission.Notify)throw new JarvisException("Dieses Tool benötigt immer eine Bestätigung.");
            var saved=new JsonObject{["permission"]=permission.ToString()};
            if(data["tenMinutes"]?.GetValue<bool>()==true)saved["expiresAt"]=DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
            await db.PutAsync(ctx.Owner(),"permissions",tool,saved,ct);await audit.AuditAsync(ctx.Owner(),"Permissions.Update","success",0,null,new(){["tool"]=tool},ct);return Results.Ok();
        });
        app.MapGet("/api/v1/approvals",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.QueryAsync("SELECT a.id,a.tool,a.args::text,a.state,a.expires_at,a.created_at,d.data->>'host' AS local_host,d.data->>'port' AS local_port,d.data->>'revision' AS local_revision FROM approvals a LEFT JOIN documents d ON d.owner=a.owner AND d.kind='local-services' AND d.id=a.args->>'serviceId' AND a.tool LIKE 'LocalNetwork.%' WHERE a.owner=$1 AND a.state='pending' AND a.expires_at>now() ORDER BY a.created_at DESC",ct,ctx.Owner()));
        app.MapPost("/api/v1/approvals/{id}",async(string id,Decision decision,HttpContext ctx,Database db,ToolDispatcher tools,CancellationToken ct)=>{
            var rows=await db.QueryAsync("UPDATE approvals SET state=$1 WHERE id=$2 AND owner=$3 AND state='pending' AND expires_at>now() RETURNING tool,args::text,task_id",ct,decision.Approve?"approved":"denied",id,ctx.Owner());
            if(rows.Count!=1)throw new JarvisException("Freigabe abgelaufen oder bereits bearbeitet.",409);
            ToolResult result;
            using var taskScope=new ExecutionScope(rows[0]["task_id"]?.GetValue<string>());
            using var localScope=rows[0]["tool"]!.GetValue<string>().StartsWith("LocalNetwork.",StringComparison.Ordinal)?new LocalNetworkScope():null;
            try { result=!decision.Approve?new ToolResult("denied"):await tools.ExecuteAsync(new(ctx.Owner()),rows[0]["tool"]!.GetValue<string>(),JsonNode.Parse(rows[0]["args"]!.GetValue<string>())!.AsObject(),id,ct); }
            catch { await db.PutAsync(ctx.Owner(),"approval-results",id,new(){["Status"]="failed_or_unknown"},CancellationToken.None);throw; }
            await db.PutAsync(ctx.Owner(),"approval-results",id,System.Text.Json.JsonSerializer.SerializeToNode(result)!.AsObject(),ct);
            return result;
        });
        app.MapGet("/api/v1/audit",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.QueryAsync("SELECT id,tool,status,duration_ms,approval,parameters::text,created_at FROM audit WHERE owner=$1 ORDER BY id DESC LIMIT 300",ct,ctx.Owner()));
        app.MapGet("/api/v1/data/{kind}",async(string kind,HttpContext ctx,Database db,CancellationToken ct)=>{Allowed(kind);return await db.ListAsync(ctx.Owner(),kind,500,ct);});
        app.MapPut("/api/v1/data/{kind}/{id}",async(string kind,string id,JsonObject data,HttpContext ctx,Database db,ToolDispatcher tools,CancellationToken ct)=>{
            if(kind is not ("contacts" or "automations" or "tasks" or "ai-models" or "event-rules"))throw new JarvisException("Schreiben nicht erlaubt.",403);
            if(kind=="ai-models"){
                if(string.IsNullOrWhiteSpace(data["model_name"]?.GetValue<string>())||data["model_name"]!.GetValue<string>().Length>150)throw new JarvisException("Modellname fehlt.");
                foreach(var price in new[]{"input_cost","output_cost","cached_input_cost"})if(data[price] is JsonNode p && (p.GetValue<decimal>()<0||p.GetValue<decimal>()>100000))throw new JarvisException("Ungültiger Modellpreis.");
                if(data["enabled"]?.GetValue<bool>()==true&&(data["validated"]?.GetValue<bool>()!=true||(data["context_window"]?.GetValue<int>()??0)<1024))throw new JarvisException("Modellfähigkeiten und Kontextfenster zuerst validieren.");
                if(data["capability_level"]?.GetValue<int>() is <0 or >4)throw new JarvisException("Capability Level muss 0–4 sein.");
                data["provider"]="configured";
            }
            if(id.Length>100||data.ToJsonString().Length>50000)throw new JarvisException("Datensatz zu groß.");
            if(kind=="tasks"){
                if((await db.GetAsync(ctx.Owner(),"tasks",id,ct))?.Data["status"]?.GetValue<string>()=="Running")throw new JarvisException("Laufende Aufgabe zuerst stoppen.",409);
                data["priority"]=Math.Clamp(data["priority"]?.GetValue<int>()??0,0,100);
                if(data["description"]?.GetValue<string>()?.Length>10000)throw new JarvisException("Aufgabe zu groß.");
            }
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
            if(kind is "tasks" or "goals") {
                var active=await db.QueryAsync("SELECT id FROM documents WHERE owner=$1 AND kind=$2 AND ($3='all' OR id=$3) AND data->>'status' IN ('Running','NeedsApproval','Queued','Planning','Active') LIMIT 1",ct,ctx.Owner(),kind,id);
                if(active.Count>0)throw new JarvisException("Aktive Aufgaben oder Ziele dürfen nicht gelöscht werden.",409);
            }
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
    private static void Allowed(string kind){if(kind is not ("contacts" or "automations" or "tasks" or "memory" or "research" or "chats" or "calls" or "notifications" or "ai-models" or "event-rules" or "events" or "goals"))throw new JarvisException("Bereich nicht freigegeben.",403);}
}
