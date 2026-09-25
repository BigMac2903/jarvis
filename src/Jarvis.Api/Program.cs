using System.Text.Json;
using System.Threading.RateLimiting;
using Jarvis.Agent;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using StackExchange.Redis;

var builder=WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=12*1024*1024);
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase;o.SerializerOptions.RespectNullableAnnotations=true;o.SerializerOptions.RespectRequiredConstructorParameters=true;});
builder.Services.AddOpenApi();
builder.Services.AddSignalR(o=>o.MaximumReceiveMessageSize=16*1024);
builder.Services.AddRateLimiter(options=>{
    options.RejectionStatusCode=429;
    options.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(context=>RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=240,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.AddFixedWindowLimiter("auth",o=>{o.PermitLimit=10;o.Window=TimeSpan.FromMinutes(1);o.QueueLimit=0;});
});
builder.Services.AddSingleton(NpgsqlDataSource.Create(builder.Configuration["DATABASE_URL"]??throw new InvalidOperationException("DATABASE_URL fehlt.")));
builder.Services.AddSingleton<IConnectionMultiplexer>(_=>ConnectionMultiplexer.Connect(builder.Configuration["REDIS_URL"]??"redis:6379,abortConnect=false"));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<IDocumentStore>(s=>s.GetRequiredService<Database>());
builder.Services.AddSingleton<Vault>();
builder.Services.AddSingleton<Settings>();
builder.Services.AddSingleton<IAuthorizationStore,AuthorizationStore>();
builder.Services.AddSingleton<IEventSink,EventSink>();
builder.Services.AddHttpClient("provider",c=>c.Timeout=TimeSpan.FromSeconds(120)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddHttpClient("browser",c=>c.Timeout=TimeSpan.FromSeconds(100));
builder.Services.AddSingleton<AiClient>();
builder.Services.AddSingleton<SemanticMemory>();
builder.Services.AddSingleton<BrowserClient>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<PhoneService>();
builder.Services.AddSingleton<IToolHandler,MemoryTools>();
builder.Services.AddSingleton<IToolHandler,WebTools>();
builder.Services.AddSingleton<IToolHandler,DeviceTools>();
builder.Services.AddSingleton<IToolHandler,ProductivityTools>();
builder.Services.AddSingleton<IToolHandler>(s=>s.GetRequiredService<PhoneService>());
builder.Services.AddSingleton<ToolDispatcher>();
builder.Services.AddSingleton<ResearchService>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddSingleton<JobWorker>();
builder.Services.AddHostedService(s=>s.GetRequiredService<JobWorker>());
builder.Services.AddHostedService<AutomationWorker>();
builder.Services.AddSingleton<IToolHandler,NotificationTools>();
var app=builder.Build();
_ = app.Services.GetRequiredService<Vault>();
await app.Services.GetRequiredService<Database>().MigrateAsync(CancellationToken.None);
app.Use(async (ctx,next)=>{
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    ctx.Response.Headers["Referrer-Policy"]="no-referrer";
    ctx.Response.Headers["Cache-Control"]="no-store";
    try { await next(ctx); }
    catch(OperationCanceledException) { if(!ctx.Response.HasStarted) ctx.Response.StatusCode=499; }
    catch(Exception e) {
        if(ctx.Response.HasStarted) throw;
        var status=e is JarvisException je?je.Status:e is JsonException or FormatException or ArgumentException?400:500;
        if(status==500) app.Logger.LogError("Request {TraceId} failed: {ErrorType}",ctx.TraceIdentifier,e.GetType().Name);
        ctx.Response.StatusCode=status;
        await ctx.Response.WriteAsJsonAsync(new{error=e is JarvisException?e.Message:status==400?"Ungültige Eingabe.":"Interner Fehler.",traceId=ctx.TraceIdentifier});
    }
});
app.UseRateLimiter();
app.UseWebSockets();
app.UseMiddleware<SessionMiddleware>();
app.MapGet("/health/live",()=>Results.Ok(new{status="ok"}));
app.MapGet("/health/ready",async(Database db,IConnectionMultiplexer redis,CancellationToken ct)=>{
    await db.QueryAsync("SELECT 1",ct); await redis.GetDatabase().PingAsync();
    return Results.Ok(new{status="ok",database=true,redis=true,migrations=true});
});
app.MapOpenApi().AddEndpointFilter(async(ctx,next)=>{
    if(ctx.HttpContext.Items["actor"] is not Actor{DeviceId:null}) return Results.Unauthorized();
    return await next(ctx);
});
app.MapHub<EventsHub>("/hubs/events");
app.MapAuth();
app.MapAdministration();
app.MapDevices();
app.MapAgent();
app.MapPhone();
app.MapPhoneRelay();
app.MapAutomationHooks();
app.Run();
public partial class Program;
