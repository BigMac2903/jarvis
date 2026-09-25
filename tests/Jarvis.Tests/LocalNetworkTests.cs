using System.Net;
using System.Text;
using Jarvis.Domain;
using Jarvis.LocalNetwork;
using Jarvis.Application;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jarvis.Tests;

public class LocalNetworkTests
{
    [Theory]
    [InlineData(null, "192.168.178.0/24")]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("192.168.178.1", "10.0.0.0/8")]
    [InlineData("172.30.254.2", "172.16.0.0/12")]
    public async Task ResolverRejectsUnapprovedDnsBeforeNetwork(string? dns,string allowlist)
    {
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["LOCAL_NETWORK_DNS"]=dns,["LOCAL_NETWORK_ALLOWLIST"]=allowlist
        }).Build();
        await Assert.ThrowsAsync<JarvisException>(()=>new LocalResolver(cfg).Resolve("nas.home",default));
    }
    [Fact]
    public async Task LiteralIpNeverRequiresDns()
    {
        var resolver=new LocalResolver(new ConfigurationBuilder().Build());
        Assert.Equal(IPAddress.Parse("192.168.178.20"),Assert.Single(await resolver.Resolve("192.168.178.20",default)));
    }
    [Theory]
    [InlineData("127.0.0.1")][InlineData("169.254.169.254")][InlineData("::1")][InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.1")][InlineData("224.0.0.1")][InlineData("8.8.8.8")][InlineData("0.0.0.0")]
    [InlineData("2130706433")][InlineData("192.168.0.1@evil.test")][InlineData("nas.local/anything")]
    public void DangerousAddressesCannotBeAllowed(string host)=>Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.Host(host));
    [Theory]
    [InlineData("0.0.0.0/0")][InlineData("10.0.0.0/7")][InlineData("172.0.0.0/8")][InlineData("169.254.0.0/16")]
    public void OverbroadNetworksRejected(string entry)=>Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.Entry(entry));
    [Theory]
    [InlineData("http://192.168.1.20:8080/")][InlineData("file:///etc/passwd")]
    [InlineData("http://user:pass@192.168.1.20/")][InlineData("http://192.168.1.20/a/../admin")]
    [InlineData("http://192.168.1.20/%2e%2e/admin")][InlineData("http://192.168.1.20/?token=secret")]
    public void UrlAndPortBypassesRejected(string url)=>Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.Target(url,[80,443]));
    [Fact]
    public void ExplicitNamesRequireNetworkPinningAndAllDnsAnswersMustMatch()
    {
        var allowed=new[]{"nas.home","192.168.178.0/24"};
        LocalNetworkPolicy.CheckAddresses("nas.home",[IPAddress.Parse("192.168.178.20")],allowed,[]);
        Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.CheckAddresses("nas.home",[IPAddress.Parse("192.168.178.20"),IPAddress.Loopback],allowed,[]));
        Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.CheckAddresses("evil.example",[IPAddress.Parse("192.168.178.20")],allowed,[]));
        Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.CheckAddresses("nas.home",[IPAddress.Parse("10.0.0.1")],allowed,[]));
        Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.CheckAddresses("nas.home",[IPAddress.Parse("192.168.178.20")],["nas.home"],[]));
        Assert.Throws<JarvisException>(()=>LocalNetworkPolicy.CheckAddresses("172.30.254.2",[IPAddress.Parse("172.30.254.2")],["172.16.0.0/12"],["172.30.254.0/28"]));
        Assert.Equal("100.64.0.0/10",LocalNetworkPolicy.Entry("100.64.0.0/10"));
        Assert.Equal("fd00::/8",LocalNetworkPolicy.Entry("fd00::/8"));
    }
    [Fact]
    public async Task HttpPinsVerifiedIpOnceAndRedactsEchoedSecret()
    {
        var dns=new Resolver();var dial=new Dialer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 18\r\nConnection: close\r\n\r\n{\"value\":\"secret\"}");
        var transport=new LocalTransport(dns,dial);
        var response=await transport.Execute(Request() with {Credential=new("Bearer","secret"),AllowHttpCredentials=true},["nas.home","192.168.178.0/24"],[80],[],false,default);
        Assert.Equal(1,dns.Calls);Assert.Equal(IPAddress.Parse("192.168.178.20"),dial.Address);
        Assert.Contains("Host: nas.home",dial.Connection!.Written);
        Assert.DoesNotContain("secret",response.Text!);Assert.Contains("[REDACTED]",response.Text!);
    }
    [Fact]
    public async Task RedirectCannotForwardCredentialsOrReachAnotherHost()
    {
        var dial=new Dialer("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/admin\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var transport=new LocalTransport(new Resolver(),dial);
        var error=await Assert.ThrowsAsync<JarvisException>(()=>transport.Execute(Request(),["nas.home","192.168.178.0/24"],[80],[],false,default));
        Assert.Equal(409,error.Status);Assert.Equal(1,dial.Calls);
    }
    [Fact]
    public async Task WorkerEnforcesBothPoliciesBeforeAnySocket()
    {
        var dial=new Dialer("");var transport=new LocalTransport(new Resolver(),dial);
        await Assert.ThrowsAsync<JarvisException>(()=>transport.Execute(Request(),["nas.home","10.0.0.0/24"],[80],[],false,default));
        await Assert.ThrowsAsync<JarvisException>(()=>transport.Execute(Request() with {Operation="CheckPort"},["nas.home","192.168.178.0/24"],[80],[],false,default));
        await Assert.ThrowsAsync<JarvisException>(()=>transport.Execute(Request() with {Credential=new("Bearer","secret")},["nas.home","192.168.178.0/24"],[80],[],false,default));
        Assert.Equal(0,dial.Calls);
    }
    [Fact]
    public async Task DispatcherDeniesLocalToolsWithoutDedicatedScope()
    {
        var handler=new Handler();var dispatcher=new ToolDispatcher([handler],new Authorization());
        await Assert.ThrowsAsync<JarvisException>(()=>dispatcher.ExecuteAsync(new("owner"),"LocalNetwork.HttpGet",new(),null,default));
        Assert.Equal(0,handler.Calls);
        using(new LocalNetworkScope())Assert.Equal("success",(await dispatcher.ExecuteAsync(new("owner"),"LocalNetwork.HttpGet",new(),null,default)).Status);
        Assert.False(LocalNetworkScope.Allowed);Assert.Equal(1,handler.Calls);
    }
    private static LocalRequest Request()=>new(){Url="http://nas.home/",Allowlist=["nas.home","192.168.178.0/24"],Ports=[80]};
    private sealed class Resolver:ILocalResolver {
        public int Calls;
        public Task<IPAddress[]> Resolve(string host,CancellationToken ct){Calls++;return Task.FromResult(new[]{IPAddress.Parse("192.168.178.20")});}
    }
    private sealed class Dialer(string response):ILocalDialer {
        public int Calls;public IPAddress? Address;public Exchange? Connection;
        public ValueTask<Stream> Connect(IPAddress ip,int port,CancellationToken ct){Calls++;Address=ip;Connection=new Exchange(response);return ValueTask.FromResult<Stream>(Connection);}
    }
    // Real HttpClient protocol processing over deterministic in-memory streams; never scans the user's LAN.
    private sealed class Exchange(string response):Stream {
        private readonly MemoryStream input=new(Encoding.ASCII.GetBytes(response));private readonly MemoryStream output=new();
        public string Written=>Encoding.ASCII.GetString(output.ToArray());
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>input.Read(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>input.ReadAsync(buffer,ct);
        public override void Write(byte[] buffer,int offset,int count)=>output.Write(buffer,offset,count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)=>output.WriteAsync(buffer,ct);
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
    private sealed class Handler:IToolHandler {
        public int Calls;public IReadOnlyList<ToolDefinition> Definitions{get;}=[new("LocalNetwork.HttpGet","test",Risk.Safe,new())];
        public Task<JsonNode?> ExecuteAsync(Actor a,string n,JsonObject j,CancellationToken ct){Calls++;return Task.FromResult<JsonNode?>(new JsonObject());}
    }
    private sealed class Authorization:IAuthorizationStore {
        public Task<Permission?> GetPermissionAsync(string o,string t,CancellationToken ct)=>Task.FromResult<Permission?>(null);
        public Task<string> RequestAsync(string o,string t,JsonObject a,CancellationToken ct)=>Task.FromResult("approval");
        public Task<bool> ConsumeAsync(string o,string i,string t,JsonObject a,CancellationToken ct)=>Task.FromResult(false);
        public Task AuditAsync(string o,string t,string s,long ms,string? id,JsonObject a,CancellationToken ct)=>Task.CompletedTask;
    }
}
