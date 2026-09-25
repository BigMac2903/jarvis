using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Jarvis.Tests;
public class SecurityTests
{
    [Fact] public void PasswordsUseSaltAndVerify()
    {
        var a=Crypto.Password("correct horse battery staple");
        var b=Crypto.Password("correct horse battery staple");
        Assert.NotEqual(a,b);Assert.True(Crypto.VerifyPassword("correct horse battery staple",a));Assert.False(Crypto.VerifyPassword("wrong",a));
        Assert.Throws<JarvisException>(()=>Crypto.Password("short"));
    }
    [Theory]
    [InlineData(Risk.Safe,null,Permission.Allow)]
    [InlineData(Risk.Confirm,null,Permission.Ask)]
    [InlineData(Risk.AlwaysConfirm,Permission.Allow,Permission.Ask)]
    [InlineData(Risk.AlwaysConfirm,Permission.Deny,Permission.Deny)]
    [InlineData(Risk.Safe,Permission.Deny,Permission.Deny)]
    public void PermissionDefaultsFailClosed(Risk risk,Permission? configured,Permission expected)=>Assert.Equal(expected,ToolDispatcher.Resolve(risk,configured));
    [Fact] public void VaultBindsCipherToOwnerAndPurpose()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["MASTER_KEY"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}).Build();
        var vault=new Vault(config,new TestDocuments());
        var encrypted=vault.Encrypt("a very private secret","owner:credential");
        Assert.DoesNotContain("private",encrypted);
        Assert.Equal("a very private secret",vault.Decrypt(encrypted,"owner:credential"));
        Assert.Throws<AuthenticationTagMismatchException>(()=>vault.Decrypt(encrypted,"other:credential"));
        var corrupt=Convert.FromBase64String(encrypted);corrupt[^1]^=1;
        Assert.Throws<AuthenticationTagMismatchException>(()=>vault.Decrypt(Convert.ToBase64String(corrupt),"owner:credential"));
    }
    [Theory]
    [InlineData("../outside.md")][InlineData("/etc/passwd.md")][InlineData("foo/../outside.md")][InlineData("file.exe")][InlineData("foo\\bar.md")]
    public void VaultPathsRejectTraversal(string path)=>Assert.Throws<JarvisException>(()=>MemoryTools.SafePath(Path.Combine(Path.GetTempPath(),"jarvis-tests"),path));
    [Fact] public void VaultPathAcceptsNestedMarkdown()=>Assert.EndsWith(Path.Combine("Projects","note.md"),MemoryTools.SafePath(Path.Combine(Path.GetTempPath(),"jarvis-tests"),"Projects/note.md"));
    [Fact] public void SchemaRejectsUnknownAndIncorrectValues()
    {
        var tool=new ToolDefinition("Test.Run","test",Risk.Safe,new(){["query"]=new("string","query",MaxLength:5)});
        Assert.Throws<JarvisException>(()=>ToolDispatcher.Validate(tool,new(){["query"]="abcdef"}));
        Assert.Throws<JarvisException>(()=>ToolDispatcher.Validate(tool,new(){["query"]=12}));
        Assert.Throws<JarvisException>(()=>ToolDispatcher.Validate(tool,new(){["query"]="ok",["sudo"]=true}));
        Assert.Throws<JarvisException>(()=>ToolDispatcher.Validate(tool,new()));
        ToolDispatcher.Validate(tool,new(){["query"]="ok"});
    }
    [Fact] public void TotpMatchesRfc6238Vector()
    {
        // RFC 6238 SHA-1 secret: ASCII 12345678901234567890; 8-digit vector 94287082 truncated to six.
        Assert.Equal("287082",Totp.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",1));
        var secret=Totp.NewSecret();var step=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;
        Assert.NotNull(Totp.Verify(secret,Totp.Code(secret,step),step-1));
        Assert.Null(Totp.Verify(secret,Totp.Code(secret,step),step));
    }
    [Fact] public async Task ApprovalPreventsExecutionAndReplays()
    {
        var handler=new CountingHandler();var authorization=new TestAuthorization();var dispatcher=new ToolDispatcher([handler],authorization);
        var actor=new Actor("alice");var args=new JsonObject{["value"]="exact"};
        var first=await dispatcher.ExecuteAsync(actor,"Test.Write",args,null,CancellationToken.None);
        Assert.Equal("approval_required",first.Status);Assert.Equal(0,handler.Calls);
        authorization.Approved=true;
        var wrong=await dispatcher.ExecuteAsync(actor,"Test.Write",new(){["value"]="changed"},"approval",CancellationToken.None);
        Assert.Equal("approval_required",wrong.Status);Assert.Equal(0,handler.Calls);
        var executed=await dispatcher.ExecuteAsync(actor,"Test.Write",args,"approval",CancellationToken.None);
        Assert.Equal("success",executed.Status);Assert.Equal(1,handler.Calls);
        var replay=await dispatcher.ExecuteAsync(actor,"Test.Write",args,"approval",CancellationToken.None);
        Assert.Equal("approval_required",replay.Status);Assert.Equal(1,handler.Calls);
        await Assert.ThrowsAsync<JarvisException>(()=>dispatcher.ExecuteAsync(new("alice","device"),"Test.Write",args,null,CancellationToken.None));
        Assert.Equal(4,authorization.Audits);
    }
    sealed class CountingHandler:IToolHandler
    {
        public int Calls;
        public IReadOnlyList<ToolDefinition> Definitions{get;}=[new("Test.Write","test",Risk.AlwaysConfirm,new(){["value"]=new("string","value")})];
        public Task<JsonNode?> ExecuteAsync(Actor a,string n,JsonObject j,CancellationToken c){Calls++;return Task.FromResult<JsonNode?>(new JsonObject{["ok"]=true});}
    }
    sealed class TestAuthorization:IAuthorizationStore
    {
        public bool Approved;public bool Consumed;public int Audits;
        public Task<Permission?> GetPermissionAsync(string o,string t,CancellationToken c)=>Task.FromResult<Permission?>(Permission.Allow);
        public Task<string> RequestAsync(string o,string t,JsonObject a,CancellationToken c)=>Task.FromResult("approval");
        public Task<bool> ConsumeAsync(string o,string id,string t,JsonObject a,CancellationToken c){var accepted=o=="alice"&&id=="approval"&&Approved&&!Consumed&&a["value"]?.GetValue<string>()=="exact";if(accepted)Consumed=true;return Task.FromResult(accepted);}
        public Task AuditAsync(string o,string t,string s,long ms,string? a,JsonObject j,CancellationToken c){Audits++;return Task.CompletedTask;}
    }
}
public sealed class TestDocuments:IDocumentStore
{
    private readonly Dictionary<(string,string,string),StoredDocument> documents=new();
    public Task<StoredDocument?> GetAsync(string o,string k,string id,CancellationToken ct)=>Task.FromResult(documents.GetValueOrDefault((o,k,id)));
    public Task<IReadOnlyList<StoredDocument>> ListAsync(string o,string k,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<StoredDocument>>(documents.Values.Where(d=>d.Owner==o&&d.Kind==k).Take(limit).ToArray());
    public Task PutAsync(string o,string k,string id,JsonObject j,CancellationToken ct){documents[(o,k,id)]=new(id,o,k,j.DeepClone().AsObject(),DateTimeOffset.UtcNow);return Task.CompletedTask;}
    public Task DeleteAsync(string o,string k,string? id,CancellationToken ct){foreach(var key in documents.Keys.Where(x=>x.Item1==o&&x.Item2==k&&(id is null||x.Item3==id)).ToArray())documents.Remove(key);return Task.CompletedTask;}
}
