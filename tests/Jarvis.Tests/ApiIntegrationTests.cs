using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jarvis.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace Jarvis.Tests;
public sealed class IntegrationFactAttribute:FactAttribute
{
    public IntegrationFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_TEST_DATABASE")))Skip="Realer PostgreSQL-Testserver fehlt (JARVIS_TEST_DATABASE).";}
}
public class ApiIntegrationTests
{
    [IntegrationFact]
    public async Task RealDatabaseSetupLoginCsrfToolsAndPairing()
    {
        var connection=Environment.GetEnvironmentVariable("JARVIS_TEST_DATABASE")!;
        if(new NpgsqlConnectionStringBuilder(connection).Database!="jarvis_test")throw new InvalidOperationException("Integrationstests benötigen eine eigene leere Datenbank jarvis_test.");
        Environment.SetEnvironmentVariable("DATABASE_URL",connection);
        Environment.SetEnvironmentVariable("REDIS_URL",Environment.GetEnvironmentVariable("JARVIS_TEST_REDIS")??"localhost:6379,abortConnect=false,connectTimeout=2000");
        Environment.SetEnvironmentVariable("MASTER_KEY",Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("PUBLIC_URL","https://localhost");
        var setup=Crypto.Token();Environment.SetEnvironmentVariable("SETUP_TOKEN",setup);
        await using var factory=new WebApplicationFactory<Program>();
        using var client=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost"),HandleCookies=true,AllowAutoRedirect=false});
        var status=(await client.GetFromJsonAsync<JsonObject>("/api/v1/setup/status"))!;
        Assert.True(status["required"]!.GetValue<bool>());
        var user="test-"+Guid.NewGuid().ToString("N");var password=Crypto.Token();
        var created=await client.PostAsJsonAsync("/api/v1/setup",new{token=setup,username=user,password,email="test@example.invalid",timezone="UTC",language="de"});
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var second=await client.PostAsJsonAsync("/api/v1/setup",new{token=setup,username="other",password,email="test@example.invalid",timezone="UTC",language="de"});
        Assert.Equal(HttpStatusCode.Conflict,second.StatusCode);
        var unauth=await client.GetAsync("/api/v1/devices");Assert.Equal(HttpStatusCode.Unauthorized,unauth.StatusCode);
        var login=await client.PostAsJsonAsync("/api/v1/auth/login",new{username=user,password});
        Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        Assert.Contains("httponly",string.Join("",login.Headers.GetValues("Set-Cookie")).ToLowerInvariant());
        var logged=(await login.Content.ReadFromJsonAsync<JsonObject>())!;
        var csrf=logged["csrf"]!.GetValue<string>();
        var noCsrf=await client.PostAsJsonAsync("/api/v1/tools/execute",new{name="Memory.Write",args=new{title="Secret",text="Persistent content"}});
        Assert.Equal(HttpStatusCode.Forbidden,noCsrf.StatusCode);
        client.DefaultRequestHeaders.Add("Origin","https://localhost");client.DefaultRequestHeaders.Add("X-CSRF-Token",csrf);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync("/api/v1/permissions/Memory.Write",new{permission="Ask"})).StatusCode);
        var action=await client.PostAsJsonAsync("/api/v1/tools/execute",new{name="Memory.Write",args=new{title="Test",text="Persistent content"}});
        var pending=(await action.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("approval_required",pending["status"]!.GetValue<string>());
        var id=pending["approvalId"]!.GetValue<string>();
        var approved=await client.PostAsJsonAsync("/api/v1/approvals/"+id,new{approve=true});Assert.Equal(HttpStatusCode.OK,approved.StatusCode);
        var duplicate=await client.PostAsJsonAsync("/api/v1/approvals/"+id,new{approve=true});Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
        var memories=await client.GetFromJsonAsync<JsonArray>("/api/v1/memory/personal");Assert.Single(memories!);
        var pairingResponse=await client.PostAsJsonAsync("/api/v1/devices/pairing",new{});var pairing=(await pairingResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        using var device=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost"),HandleCookies=false});
        var paired=await device.PostAsJsonAsync("/api/v1/devices/pair",new{token=pairing["token"]!.GetValue<string>(),code=pairing["code"]!.GetValue<string>(),name="Unit device",platform="ios"});
        Assert.Equal(HttpStatusCode.OK,paired.StatusCode);
        var deviceCredentials=(await paired.Content.ReadFromJsonAsync<JsonObject>())!;
        device.DefaultRequestHeaders.Authorization=new("Bearer",deviceCredentials["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden,(await device.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await device.PostAsJsonAsync("/api/v1/device-agent/location",new{latitude=52.5,longitude=13.4})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await device.PostAsJsonAsync("/api/v1/device-agent/location",new{latitude=999,longitude=13.4})).StatusCode);
        var db=factory.Services.GetRequiredService<Database>();
        var rows=await db.QueryAsync("SELECT parameters::text FROM audit",CancellationToken.None);
        Assert.DoesNotContain(rows,r=>r.ToJsonString().Contains("Persistent content"));
        using var scope=factory.Services.CreateScope();
        var ready=await client.GetAsync("/health/ready");Assert.Equal(HttpStatusCode.OK,ready.StatusCode);
    }
}
