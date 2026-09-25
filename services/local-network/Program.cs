using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Jarvis.Domain;
using Microsoft.AspNetCore.RateLimiting;

namespace Jarvis.LocalNetwork;
public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8_000_000);
        var token = builder.Configuration["LOCAL_NETWORK_TOKEN"] ?? "";
        if (token.Length < 32) throw new InvalidOperationException("LOCAL_NETWORK_TOKEN must contain at least 32 characters.");
        string[] List(string key) => (builder.Configuration[key] ?? "").Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ceiling = List("LOCAL_NETWORK_ALLOWLIST").Select(LocalNetworkPolicy.Entry).ToArray();
        var ports = List("LOCAL_NETWORK_PORTS").Select(int.Parse).Where(p => p is > 0 and <= 65535).Distinct().ToArray();
        // Control plane is never a permitted target, even if a broad LAN range overlaps it.
        var denied = List("LOCAL_NETWORK_DENYLIST").Concat(["172.30.254.0/28", "172.30.254.16/28"]).ToArray();
        var enabled = builder.Configuration["LOCAL_NETWORK_ENABLED"] == "true";
        var probes = builder.Configuration["LOCAL_NETWORK_PROBES_ENABLED"] == "true";
        builder.Services.AddSingleton<ILocalResolver, LocalResolver>(); builder.Services.AddSingleton<ILocalDialer, LocalDialer>(); builder.Services.AddSingleton<LocalTransport>();
        builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext,string>(_ => RateLimitPartition.GetTokenBucketLimiter("global", _ => new TokenBucketRateLimiterOptions {
            TokenLimit = 2, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(2), QueueLimit = 0, AutoReplenishment = true })); });
        var app = builder.Build();
        app.Use(async (ctx, next) => {
            ctx.Response.Headers.CacheControl = "no-store";
            try {
                if (ctx.Request.Path != "/health/live") {
                    var supplied = ctx.Request.Headers["X-Service-Token"].ToString();
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(supplied))) throw new JarvisException("Dienstauthentifizierung erforderlich.", 401);
                }
                await next(ctx);
            } catch (Exception e) {
                ctx.Response.StatusCode = e is JarvisException j ? j.Status : e is OperationCanceledException ? 504 : 502;
                await ctx.Response.WriteAsJsonAsync(new { error = e is JarvisException ? e.Message : "Lokaler Zugriff fehlgeschlagen; keine Credentials oder Zielinhalte im Fehlerprotokoll." });
            }
        });
        app.UseRateLimiter();
        app.MapGet("/health/live", () => new { status = "ok" }).DisableRateLimiting();
        app.MapGet("/policy", () => new { enabled, allowlist = ceiling, ports, denied, probes, dns = builder.Configuration["LOCAL_NETWORK_DNS"] });
        app.MapPost("/execute", async (LocalRequest request, LocalTransport transport, CancellationToken ct) => {
            if (!enabled) throw new JarvisException("Lokaler Netzwerkzugriff ist serverseitig deaktiviert.", 403);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await transport.Execute(request, ceiling, ports, denied, probes, timeout.Token);
        });
        app.Run();
    }
}
