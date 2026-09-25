using Jarvis.Domain;
using Jarvis.Infrastructure;
namespace Jarvis.Api;
public sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx,Database db,IConfiguration config)
    {
        var path=ctx.Request.Path.Value??"";
        if(path.StartsWith("/internal/sip/",StringComparison.Ordinal)) {
            var token=config["SIP_SERVICE_TOKEN"]??"";
            if(token.Length<32||!Crypto.EqualsSecret(ctx.Request.Headers["X-Service-Token"].ToString(),token))throw new JarvisException("Dienstauthentifizierung erforderlich.",401);
            await next(ctx);return;
        }
        var anonymous=path.StartsWith("/health/")||path=="/api/v1/setup/status"||path=="/api/v1/setup"||path=="/api/v1/auth/login"||
            path=="/api/v1/devices/pair"||(path.StartsWith("/api/v1/oauth/")&&path.EndsWith("/callback"))||path.StartsWith("/api/v1/phone/webhook/")||path.StartsWith("/api/v1/phone/relay/")||path.StartsWith("/api/v1/automation-webhooks/");
        if(anonymous) { await next(ctx); return; }
        var ct=ctx.RequestAborted;
        if(ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ",StringComparison.Ordinal))
        {
            var token=ctx.Request.Headers.Authorization.ToString()[7..];
            var rows=await db.QueryAsync("SELECT owner,device_id FROM device_tokens WHERE hash=$1 AND revoked=false",ct,Crypto.Hash(token));
            if(rows.Count==1) ctx.Items["actor"]=new Actor(rows[0]["owner"]!.GetValue<string>(),rows[0]["device_id"]!.GetValue<string>());
        }
        else if(ctx.Request.Cookies.TryGetValue("jarvis_session",out var session))
        {
            var rows=await db.QueryAsync("SELECT user_id,csrf FROM sessions WHERE token_hash=$1 AND expires_at>now() AND absolute_expires_at>now()",ct,Crypto.Hash(session));
            if(rows.Count==1)
            {
                ctx.Items["actor"]=new Actor(rows[0]["user_id"]!.GetValue<string>());
                ctx.Items["csrf"]=rows[0]["csrf"]!.GetValue<string>();
                if(ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
                {
                    var origin=ctx.Request.Headers.Origin.ToString();
                    var expected=new Uri(config["PUBLIC_URL"]!).GetLeftPart(UriPartial.Authority);
                    if(origin!=expected||!Crypto.EqualsSecret(ctx.Request.Headers["X-CSRF-Token"].ToString(),(string)ctx.Items["csrf"]!)) throw new JarvisException("CSRF-Prüfung fehlgeschlagen.",403);
                }
                if(ctx.WebSockets.IsWebSocketRequest && ctx.Request.Headers.Origin.ToString()!=new Uri(config["PUBLIC_URL"]!).GetLeftPart(UriPartial.Authority)) throw new JarvisException("WebSocket-Origin ungültig.",403);
            }
        }
        if(ctx.Items["actor"] is not Actor) throw new JarvisException("Anmeldung erforderlich.",401);
        if(ctx.Items["actor"] is Actor{DeviceId:not null} && !path.StartsWith("/api/v1/device-agent/")) throw new JarvisException("Gerätetoken für diesen Endpunkt nicht berechtigt.",403);
        await next(ctx);
    }
}
public static class ContextExtensions
{
    public static Actor Actor(this HttpContext ctx)=>ctx.Items["actor"] as Actor??throw new JarvisException("Anmeldung erforderlich.",401);
    public static string Owner(this HttpContext ctx) {
        var actor=ctx.Actor(); if(actor.DeviceId is not null) throw new JarvisException("Benutzeranmeldung erforderlich.",403); return actor.UserId;
    }
}
