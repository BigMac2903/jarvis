using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Jarvis.Sip;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
var serviceToken = builder.Configuration["SIP_SERVICE_TOKEN"] ?? "";
if (serviceToken.Length < 32) throw new InvalidOperationException("SIP_SERVICE_TOKEN fehlt.");
builder.Services.AddHttpClient("core", c => {
    c.BaseAddress = new Uri(builder.Configuration["CORE_URL"] ?? "http://jarvis-api:8080");
    c.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<SipEngine>();
builder.Services.AddHostedService(s => s.GetRequiredService<SipEngine>());
var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ctx.Request.Path != "/health/live" && !CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Service-Token"].ToString())),
        SHA256.HashData(Encoding.UTF8.GetBytes(serviceToken)))) { ctx.Response.StatusCode = 401; return; }
    try { await next(ctx); }
    catch (Exception e) when (!ctx.Response.HasStarted) {
        ctx.Response.StatusCode = e is JarvisException je ? je.Status : 500;
        await ctx.Response.WriteAsJsonAsync(new { error = e is JarvisException ? e.Message : "SIP operation failed" });
    }
});
app.MapGet("/health/live", () => new { status = "ok" });
app.MapGet("/accounts", (string owner, SipEngine engine) => engine.Health(owner));
app.MapPost("/calls", (SipDial request, SipEngine engine) => engine.Dial(request));
app.MapPost("/calls/{id}/{operation}", (string id, string operation, JsonObject args, SipEngine engine, CancellationToken ct) => engine.Control(id, operation, args, ct));
app.Run();
