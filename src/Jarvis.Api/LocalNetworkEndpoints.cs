using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Agent;
using Jarvis.Domain;
using Jarvis.Infrastructure;

namespace Jarvis.Api;
public static class LocalNetworkEndpoints
{
    public record StageFile(string FileName,string ContentBase64);
    public static void MapLocalNetwork(this WebApplication app)
    {
        app.MapGet("/api/v1/local-network/policy",(LocalNetworkTools tools,CancellationToken ct)=>tools.Policy(ct));
        app.MapGet("/api/v1/local-network/services",async(HttpContext ctx,Database db,CancellationToken ct)=> {
            var statuses=await db.ListAsync(ctx.Owner(),"local-service-status",100,ct);
            return (await db.ListAsync(ctx.Owner(),"local-services",100,ct)).Select(s=>new {s.Id,service=s.Data,status=statuses.FirstOrDefault(x=>x.Id==s.Id)?.Data});
        });
        app.MapPut("/api/v1/local-network/services/{id}",async(string id,LocalService body,HttpContext ctx,Database db,IAuthorizationStore audit,CancellationToken ct)=> {
            if(!Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,60}$"))throw new JarvisException("Dienst-ID ungültig.");
            body.Validate();
            body=body with {Revision=Guid.NewGuid().ToString("N")};
            var saved=JsonSerializer.SerializeToNode(body,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            if((await db.ListAsync(ctx.Owner(),"local-services",101,ct)).Count>=100 && await db.GetAsync(ctx.Owner(),"local-services",id,ct) is null)throw new JarvisException("Maximal 100 registrierte Dienste.");
            await db.PutAsync(ctx.Owner(),"local-services",id,saved,ct);
            await db.DeleteAsync(ctx.Owner(),"local-discovery",id,ct);
            await audit.AuditAsync(ctx.Owner(),"LocalNetwork.RegisterService","success",0,null,new(){["id"]=id},ct);return Results.Ok();
        });
        app.MapDelete("/api/v1/local-network/services/{id}",async(string id,HttpContext ctx,Database db,IAuthorizationStore audit,CancellationToken ct)=> {
            await db.DeleteAsync(ctx.Owner(),"local-services",id,ct);await db.DeleteAsync(ctx.Owner(),"local-service-status",id,ct);
            await db.DeleteAsync(ctx.Owner(),"local-discovery",id,ct);
            await audit.AuditAsync(ctx.Owner(),"LocalNetwork.RemoveService","success",0,null,new(){["id"]=id},ct);return Results.Ok();
        });
        app.MapPut("/api/v1/local-network/credentials/{id}",async(string id,LocalCredential body,HttpContext ctx,Vault vault,IAuthorizationStore audit,CancellationToken ct)=> {
            if(!Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,60}$") || body.Type is not ("Basic" or "Bearer" or "Header") || body.Value.Length is <1 or >8192 || body.Value.Any(char.IsControl) || body.Username.Length>200 || body.Username.Any(char.IsControl))throw new JarvisException("Credential ungültig.");
            if(body.Type=="Header" && body.Header is not ("X-Api-Key" or "Api-Key" or "X-Auth-Token"))throw new JarvisException("Header nicht erlaubt.");
            await vault.PutAsync(ctx.Owner(),"local-network."+id,JsonSerializer.Serialize(body),ct);
            await audit.AuditAsync(ctx.Owner(),"LocalNetwork.StoreCredential","success",0,null,new(){["reference"]=id},ct);return Results.Ok();
        });
        app.MapDelete("/api/v1/local-network/credentials/{id}",async(string id,HttpContext ctx,Database db,CancellationToken ct)=> {
            if(!Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,60}$"))throw new JarvisException("Referenz ungültig.");
            await db.DeleteAsync(ctx.Owner(),"secrets","local-network."+id,ct);return Results.Ok();
        });
        app.MapPost("/api/v1/local-network/chat",async(AgentEndpoints.ChatRequest body,HttpContext ctx,Orchestrator agent,Settings settings,CancellationToken ct)=> {
            await settings.RequireAsync(ctx.Owner(),"local-network",ct);
            if((await settings.GetAsync(ctx.Owner(),"local-network",ct))["allowModelContext"]?.GetValue<bool>()!=true)throw new JarvisException("Übermittlung lokaler Ergebnisse an den KI-Anbieter zuerst ausdrücklich freigeben.",403);
            var conversation=body.Conversation??Guid.NewGuid().ToString("N");
            if(!Regex.IsMatch(conversation,"^[a-zA-Z0-9_-]{1,80}$"))throw new JarvisException("Gesprächs-ID ungültig.");
            using var scope=new LocalNetworkScope();return await agent.ChatAsync(ctx.Owner(),"local-"+conversation,body.Message,null,ct);
        });
        app.MapPost("/api/v1/local-network/files",async(StageFile file,HttpContext ctx,LocalNetworkTools tools,CancellationToken ct)=> {
            if(file.ContentBase64.Length>7_000_000)throw new JarvisException("Datei zu groß.",413);
            return await tools.SaveFile(ctx.Owner(),file.FileName,Convert.FromBase64String(file.ContentBase64),"user-upload",ct);
        });
        app.MapGet("/api/v1/local-network/files",(HttpContext ctx,Database db,CancellationToken ct)=>db.QueryAsync("SELECT id,(data-'cipher')::text AS info FROM documents WHERE owner=$1 AND kind='local-files' ORDER BY updated_at DESC LIMIT 10",ct,ctx.Owner()));
        app.MapGet("/api/v1/local-network/files/{id}",async(string id,HttpContext ctx,LocalNetworkTools tools,CancellationToken ct)=> {
            var file=await tools.ReadFile(ctx.Owner(),id,ct);ctx.Response.Headers.ContentSecurityPolicy="sandbox";
            return Results.File(file.Bytes,"application/octet-stream",file.Name);
        });
        app.MapDelete("/api/v1/local-network/files/{id}",async(string id,HttpContext ctx,Database db,CancellationToken ct)=> {await db.DeleteAsync(ctx.Owner(),"local-files",id,ct);return Results.Ok();});
    }
}
