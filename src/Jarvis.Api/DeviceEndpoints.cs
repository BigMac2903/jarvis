using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Npgsql;
namespace Jarvis.Api;
public static class DeviceEndpoints
{
    public record PairRequest(string Token,string Code,string Name,string Platform);
    public static void MapDevices(this WebApplication app)
    {
        app.MapGet("/api/v1/devices",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.ListAsync(ctx.Owner(),"devices",100,ct));
        app.MapPost("/api/v1/devices/pairing",async(HttpContext ctx,Database db,CancellationToken ct)=>{
            var token=Crypto.Token();var code=RandomNumberGenerator.GetInt32(0,1000000).ToString("D6");var expires=DateTimeOffset.UtcNow.AddMinutes(5);
            await db.ExecuteAsync("INSERT INTO pairings(token_hash,owner,code_hash,expires_at) VALUES($1,$2,$3,$4)",ct,Crypto.Hash(token),ctx.Owner(),Crypto.Hash(code),expires);
            return new{token,code,expiresAt=expires};
        });
        app.MapPost("/api/v1/devices/pair",async(PairRequest input,Database db,CancellationToken ct)=>{
            if(input.Name.Length is <1 or >100||input.Platform is not ("windows" or "macos" or "ios"))throw new JarvisException("Ungültiges Gerät.");
            var id=Guid.NewGuid().ToString("N");var token=Crypto.Token();string owner;
            await using var connection=await db.Source.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
            await using(var claim=new NpgsqlCommand("UPDATE pairings SET used=true WHERE token_hash=$1 AND code_hash=$2 AND used=false AND expires_at>now() RETURNING owner",connection,tx)){
                claim.Parameters.AddWithValue(Crypto.Hash(input.Token));claim.Parameters.AddWithValue(Crypto.Hash(input.Code));
                owner=(string?)await claim.ExecuteScalarAsync(ct)??throw new JarvisException("Pairing-Code oder Token ungültig.",403);
            }
            await using(var insert=new NpgsqlCommand("INSERT INTO device_tokens(hash,owner,device_id) VALUES($1,$2,$3)",connection,tx)){
                insert.Parameters.AddWithValue(Crypto.Hash(token));insert.Parameters.AddWithValue(owner);insert.Parameters.AddWithValue(id);await insert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await db.PutAsync(owner,"devices",id,new(){["name"]=input.Name,["platform"]=input.Platform,["lastSeen"]=DateTimeOffset.UtcNow.ToString("O"),["revoked"]=false},ct);
            return new{deviceId=id,token};
        }).RequireRateLimiting("auth");
        app.MapPost("/api/v1/devices/{id}/revoke",async(string id,HttpContext ctx,Database db,CancellationToken ct)=>{
            await db.ExecuteAsync("UPDATE device_tokens SET revoked=true WHERE owner=$1 AND device_id=$2",ct,ctx.Owner(),id);
            var doc=await db.GetAsync(ctx.Owner(),"devices",id,ct);if(doc is not null){doc.Data["revoked"]=true;await db.PutAsync(ctx.Owner(),"devices",id,doc.Data,ct);}return Results.Ok();
        });
        app.MapPost("/api/v1/device-agent/status",async(JsonObject input,HttpContext ctx,Database db,IEventSink events,CancellationToken ct)=>{
            var actor=Device(ctx);var doc=await db.GetAsync(actor.UserId,"devices",actor.DeviceId!,ct)??throw new JarvisException("Gerät fehlt.",404);
            foreach(var key in new[]{"computerName","resolution","activeApplication","battery","online"})if(input[key] is JsonNode value&&value.ToJsonString().Length<1000)doc.Data[key]=value.DeepClone();
            doc.Data["lastSeen"]=DateTimeOffset.UtcNow.ToString("O");
            await db.PutAsync(actor.UserId,"devices",actor.DeviceId!,doc.Data,ct);await events.SendAsync(actor.UserId,"device",new{id=actor.DeviceId},ct);return Results.Ok();
        });
        app.MapPost("/api/v1/device-agent/location",async(JsonObject input,HttpContext ctx,Database db,CancellationToken ct)=>{
            var actor=Device(ctx);var lat=input["latitude"]?.GetValue<double>()??999;var lon=input["longitude"]?.GetValue<double>()??999;
            if(!double.IsFinite(lat)||!double.IsFinite(lon)||lat is <-90 or >90||lon is <-180 or >180)throw new JarvisException("Standort ungültig.");
            var doc=await db.GetAsync(actor.UserId,"devices",actor.DeviceId!,ct)??throw new JarvisException("Gerät fehlt.",404);
            doc.Data["location"]=new JsonObject{["latitude"]=lat,["longitude"]=lon,["receivedAt"]=DateTimeOffset.UtcNow.ToString("O")};
            await db.PutAsync(actor.UserId,"devices",actor.DeviceId!,doc.Data,ct);return Results.Ok();
        });
        app.MapPost("/api/v1/device-agent/event",async(JsonObject input,HttpContext ctx,Database db,CancellationToken ct)=>{
            var actor=Device(ctx);var name=input["name"]?.GetValue<string>()??"";
            if(name.Length is <1 or >100)throw new JarvisException("Ereignisname fehlt.");
            await db.PutAsync(actor.UserId,"device-events",Guid.NewGuid().ToString("N"),new(){["deviceId"]=actor.DeviceId,["name"]=name,["receivedAt"]=DateTimeOffset.UtcNow.ToString("O")},ct);
            return Results.Ok();
        });
        app.MapGet("/api/v1/device-agent/commands",async(HttpContext ctx,Database db,CancellationToken ct)=>{
            var actor=Device(ctx);
            return await db.QueryAsync("UPDATE commands SET state='delivered' WHERE id IN (SELECT id FROM commands WHERE owner=$1 AND device_id=$2 AND state='pending' AND expires_at>now() ORDER BY created_at LIMIT 5 FOR UPDATE SKIP LOCKED) RETURNING id,command,args::text,expires_at",ct,actor.UserId,actor.DeviceId!);
        });
        app.MapPost("/api/v1/device-agent/commands/{id}/result",async(string id,JsonObject result,HttpContext ctx,Database db,CancellationToken ct)=>{
            var actor=Device(ctx);if(result.ToJsonString().Length>10000000)throw new JarvisException("Ergebnis zu groß.");
            var count=await db.ExecuteAsync("UPDATE commands SET state=$1,result=$2::jsonb WHERE id=$3 AND owner=$4 AND device_id=$5 AND state='delivered' AND expires_at>now()",ct,result["ok"]?.GetValue<bool>()==true?"completed":"failed",result.ToJsonString(),id,actor.UserId,actor.DeviceId!);
            if(count!=1)throw new JarvisException("Kommando abgelaufen oder bereits beantwortet.",409);return Results.Ok();
        });
    }
    private static Actor Device(HttpContext ctx){var actor=ctx.Actor();if(actor.DeviceId is null)throw new JarvisException("Gerätetoken erforderlich.",403);return actor;}
}
