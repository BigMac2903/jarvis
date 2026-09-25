using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Npgsql;
namespace Jarvis.Api;
public static class AuthEndpoints
{
    public record Setup(string Token,string Username,string Password,string Email,string Timezone,string Language,Dictionary<string,JsonObject>? Settings=null);
    public record Login(string Username,string Password,string? Code=null);
    public record MfaCode(string Code);
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/v1/setup/status",async(Database db,CancellationToken ct)=>new{required=(await db.QueryAsync("SELECT id FROM users LIMIT 1",ct)).Count==0});
        app.MapPost("/api/v1/setup",async(Setup input,Database db,Settings settings,IConfiguration cfg,CancellationToken ct)=>{
            if(string.IsNullOrWhiteSpace(cfg["SETUP_TOKEN"])||!Crypto.EqualsSecret(input.Token,cfg["SETUP_TOKEN"]!)) throw new JarvisException("Setup-Token ungültig.",403);
            if(input.Username.Length is <3 or >80||input.Email.Length>320||input.Language.Length>20) throw new JarvisException("Ungültige Benutzerdaten.");
            _=TimeZoneInfo.FindSystemTimeZoneById(input.Timezone);
            var hash=Crypto.Password(input.Password); var id=Guid.NewGuid().ToString("N");
            await using var connection=await db.Source.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(ct);
            await using(var command=new NpgsqlCommand("SELECT pg_advisory_xact_lock(826416)",connection,tx)) await command.ExecuteNonQueryAsync(ct);
            await using(var command=new NpgsqlCommand("SELECT count(*) FROM users",connection,tx))
                if((long)(await command.ExecuteScalarAsync(ct))!>0) throw new JarvisException("Setup bereits abgeschlossen.",409);
            await using(var command=new NpgsqlCommand("INSERT INTO users(id,username,password_hash,email,timezone,language) VALUES($1,$2,$3,$4,$5,$6)",connection,tx)){
                foreach(var value in new[]{id,input.Username,hash,input.Email,input.Timezone,input.Language}) command.Parameters.AddWithValue(value);
                await command.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            if(input.Settings is not null) foreach(var section in input.Settings) await settings.SaveAsync(id,section.Key,section.Value,ct);
            return Results.Ok(new{ok=true});
        }).RequireRateLimiting("auth");
        app.MapPost("/api/v1/auth/login",async(Login input,HttpContext ctx,Database db,Vault vault,CancellationToken ct)=>{
            var users=await db.QueryAsync("SELECT id,password_hash,totp_secret,totp_last_step FROM users WHERE username=$1",ct,input.Username);
            var dummy="pbkdf2-sha512$210000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=$"+Convert.ToBase64String(new byte[64]);
            var valid=Crypto.VerifyPassword(input.Password,users.Count==1?users[0]["password_hash"]!.GetValue<string>():dummy);
            if(users.Count!=1||!valid) throw new JarvisException("Anmeldedaten ungültig.",401);
            var user=users[0]; var owner=user["id"]!.GetValue<string>();
            if(user["totp_secret"] is JsonNode secret)
            {
                var step=Totp.Verify(vault.Decrypt(secret.GetValue<string>(),owner+":totp"),input.Code??"",user["totp_last_step"]!.GetValue<long>());
                if(step is null||await db.ExecuteAsync("UPDATE users SET totp_last_step=$1 WHERE id=$2 AND totp_last_step<$1",ct,step.Value,owner)!=1) throw new JarvisException("MFA-Code erforderlich oder ungültig.",401);
            }
            return await Session(ctx,db,owner,ct);
        }).RequireRateLimiting("auth");
        app.MapGet("/api/v1/auth/me",async(HttpContext ctx,Database db,CancellationToken ct)=>{
            var user=(await db.QueryAsync("SELECT id,username,email,timezone,language,(totp_secret IS NOT NULL) AS mfa FROM users WHERE id=$1",ct,ctx.Owner()))[0];
            return new{user,csrf=ctx.Items["csrf"]};
        });
        app.MapPost("/api/v1/auth/refresh",async(HttpContext ctx,Database db,CancellationToken ct)=>{
            var old=ctx.Request.Cookies["jarvis_session"]!;
            var rows=await db.QueryAsync("DELETE FROM sessions WHERE token_hash=$1 AND user_id=$2 RETURNING absolute_expires_at",ct,Crypto.Hash(old),ctx.Owner());
            if(rows.Count!=1) throw new JarvisException("Sitzung abgelaufen.",401);
            return await Session(ctx,db,ctx.Owner(),ct,rows[0]["absolute_expires_at"]!.GetValue<DateTime>());
        });
        app.MapPost("/api/v1/auth/logout",async(HttpContext ctx,Database db,CancellationToken ct)=>{
            await db.ExecuteAsync("DELETE FROM sessions WHERE token_hash=$1",ct,Crypto.Hash(ctx.Request.Cookies["jarvis_session"]??""));
            ctx.Response.Cookies.Delete("jarvis_session",Cookie()); return Results.Ok();
        });
        app.MapPost("/api/v1/auth/mfa/enroll",async(HttpContext ctx,Database db,Vault vault,CancellationToken ct)=>{
            var secret=Totp.NewSecret();
            if(await db.ExecuteAsync("UPDATE users SET totp_pending=$1 WHERE id=$2 AND totp_secret IS NULL",ct,vault.Encrypt(secret,ctx.Owner()+":totp"),ctx.Owner())!=1)throw new JarvisException("MFA ist bereits aktiv.",409);
            return new{secret,uri="otpauth://totp/JARVIS:"+ctx.Owner()+"?secret="+secret+"&issuer=JARVIS&digits=6&period=30"};
        });
        app.MapPost("/api/v1/auth/mfa/confirm",async(MfaCode input,HttpContext ctx,Database db,Vault vault,CancellationToken ct)=>{
            var row=(await db.QueryAsync("SELECT totp_pending FROM users WHERE id=$1",ct,ctx.Owner()))[0];
            if(row["totp_pending"] is not JsonNode pending||Totp.Verify(vault.Decrypt(pending.GetValue<string>(),ctx.Owner()+":totp"),input.Code,-1) is not long step) throw new JarvisException("MFA-Code ungültig.");
            await db.ExecuteAsync("UPDATE users SET totp_secret=totp_pending,totp_pending=NULL,totp_last_step=$1 WHERE id=$2",ct,step,ctx.Owner());
            return Results.Ok(new{ok=true});
        });
    }
    private static CookieOptions Cookie()=>new(){HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,Path="/",IsEssential=true};
    private static async Task<IResult> Session(HttpContext ctx,Database db,string owner,CancellationToken ct,DateTimeOffset? absolute=null)
    {
        var token=Crypto.Token();var csrf=Crypto.Token();
        var end=absolute??DateTimeOffset.UtcNow.AddDays(7);var expires=DateTimeOffset.UtcNow.AddHours(8);
        if(expires>end)expires=end;
        await db.ExecuteAsync("INSERT INTO sessions(token_hash,user_id,csrf,expires_at,absolute_expires_at) VALUES($1,$2,$3,$4,$5)",ct,Crypto.Hash(token),owner,csrf,expires,end);
        var cookie=Cookie();cookie.Expires=end;ctx.Response.Cookies.Append("jarvis_session",token,cookie);
        return Results.Ok(new{csrf,expiresAt=expires});
    }
}
