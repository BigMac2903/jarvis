using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public class OperatorTests
{
    [Fact]
    public void CompatibleProtocolGroupsParallelToolCallsBeforeResults()
    {
        var input = new JsonArray();
        foreach (var id in new[] { "one", "two" }) input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = "Test", ["arguments"] = "{}" });
        foreach (var id in new[] { "one", "two" }) input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = "ok" });
        var messages = AiClient.CompatibleMessages(input);
        Assert.Equal(3, messages.Count); Assert.Equal(2, messages[0]!["tool_calls"]!.AsArray().Count);
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
    }
    [Fact]
    public async Task PersonalMemoryEncryptsVersionsAndExcludesSensitiveContext()
    {
        var store = new TestDocuments();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["MASTER_KEY"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }).Build();
        var memory = new PersonalMemory(store, new Vault(config, store));
        var fact = new JsonObject { ["category"] = "Identity", ["fact"] = "Meine private Adresse", ["source"] = "User", ["sensitive"] = false };
        await memory.SaveAsync("alice", "entry", fact, default);
        var stored = await store.GetAsync("alice", "personal-memory", "entry", default);
        Assert.DoesNotContain("Adresse", stored!.Data.ToJsonString());
        Assert.Empty(await memory.ListAsync("alice", false, default));
        Assert.Single(await memory.ListAsync("alice", true, default));
        fact["fact"] = "Meine neue private Adresse";
        await memory.SaveAsync("alice", "entry", fact, default);
        var history = await store.ListAsync("alice", "memory-history", 10, default);
        Assert.Single(history); Assert.DoesNotContain("Adresse", history[0].Data.ToJsonString());
        Assert.Equal("Meine private Adresse", memory.Reveal("alice", "entry", history[0].Data)["fact"]!.GetValue<string>());
    }
    [Fact]
    public void EventsRespectQuietHoursAndExplicitCallingOptIn()
    {
        var rule = new JsonObject { ["urgency"] = 50, ["impact"] = 50, ["relevance"] = 50 };
        var cfg = new JsonObject { ["quietStart"] = 22, ["quietEnd"] = 6, ["criticalCalls"] = true };
        Assert.True(Jarvis.Agent.EventPolicy.Evaluate(rule, cfg, 23).Deferred);
        Assert.False(Jarvis.Agent.EventPolicy.Evaluate(rule, cfg, 12).Deferred);
        rule["urgency"] = 100; rule["impact"] = 100; rule["relevance"] = 100;
        Assert.Equal("Notify", Jarvis.Agent.EventPolicy.Evaluate(rule, cfg, 23).Action);
        rule["allowCall"] = true;
        Assert.Equal("Call", Jarvis.Agent.EventPolicy.Evaluate(rule, cfg, 23).Action);
        Assert.False(Jarvis.Agent.EventPolicy.Evaluate(rule, cfg, 23).Deferred);
    }
    [Theory]
    [InlineData("+499001234567")]
    [InlineData("+19001234567")]
    [InlineData("sip:evil@example.org")]
    [InlineData("+441234567890")]
    public void PhoneDeniesInvalidOrUnapprovedDestinations(string number) =>
        Assert.Throws<JarvisException>(() => PhonePolicy.Number(number, new() { ["allowedCountries"] = new JsonArray("+49") }));
    [Fact]
    public void PhoneRoutesByGroupAndNightWindow()
    {
        var cfg = new JsonObject { ["defaultAccount"] = "private", ["routingRules"] = new JsonArray(new JsonObject {
            ["accountId"] = "business", ["prefix"] = "+49", ["contactGroup"] = "company", ["startHour"] = 20, ["endHour"] = 6 }) };
        Assert.Equal("business", PhonePolicy.Route(cfg, [], "+493012345678", "private", "company", "call", 23));
        Assert.Equal("private", PhonePolicy.Route(cfg, [], "+493012345678", "private", "company", "call", 12));
        Assert.Equal(90, PhonePolicy.Duration(new() { ["max_duration"] = 2000 }, new() { ["maxCallDuration"] = 90 }));
    }
    [Fact]
    public void SipRejectsHeaderInjectionAndInsecureSdes()
    {
        Assert.Throws<JarvisException>(() => new SipAccount { Server = "pbx.example", Username = "user\r\nVia: evil" }.Validate());
        Assert.Throws<JarvisException>(() => new SipAccount { Server = "pbx.example", Username = "user", Transport = "UDP", UseTls = false, Srtp = true }.Validate());
        new SipAccount { Server = "pbx.example", Username = "user" }.Validate();
    }
    [Theory]
    [InlineData("../secret")]
    [InlineData("%2e%2e/secret")]
    [InlineData("folder\\secret")]
    [InlineData("https://evil.example/file")]
    public void ConnectorPathCannotEscape(string path) => Assert.Throws<JarvisException>(() => ConnectorHttp.Path(path));
    [Fact]
    public void DavRejectsExternalEntities()
    {
        var xml = "<!DOCTYPE r [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><r>&secret;</r>";
        Assert.Throws<System.Xml.XmlException>(() => NextcloudTools.ParseListing(Encoding.UTF8.GetBytes(xml), new Uri("https://cloud.example/remote.php/dav/files/me/")));
    }
    [Fact]
    public void ChatGptImportIncludesOnlyUserStatementsAndDeduplicates()
    {
        var input = "[{\"mapping\":{\"1\":{\"message\":{\"author\":{\"role\":\"user\"},\"content\":{\"parts\":[\"Ich mag Kaffee.\",\"Ich mag Kaffee.\"]}}},\"2\":{\"message\":{\"author\":{\"role\":\"assistant\"},\"content\":{\"parts\":[\"Erfundene Behauptung\"]}}}}}]";
        var result = PersonalMemory.ImportCandidates(input, "json");
        Assert.Single(result); Assert.Equal("Ich mag Kaffee.", result[0]!["fact"]!.GetValue<string>());
        Assert.True(result[0]!["sensitive"]!.GetValue<bool>());
        Assert.Equal("review", result[0]!["status"]!.GetValue<string>());
    }
    [Fact]
    public void RouterExcludesUnvalidatedAndFrontierForRoutine()
    {
        JsonObject Model(string name, int level, decimal price, bool validated = true) => new() { ["id"] = name, ["model_name"] = name,
            ["enabled"] = true, ["validated"] = validated, ["supports_tools"] = true, ["context_window"] = 100000,
            ["capability_level"] = level, ["input_cost"] = price, ["output_cost"] = price };
        var result = ModelRouting.Select([Model("cheap", 0, 1), Model("balanced", 1, 2), Model("frontier", 3, 10), Model("unknown", 0, 0, false)],
            ModelRouting.Classify("Hallo", 100, 1, false), "best_value", false);
        Assert.Equal("cheap", result[0].Model); Assert.DoesNotContain(result, m => m.Model is "unknown" or "frontier");
        Assert.Equal(0.0025m, ModelRouting.Cost(1000, 500, 1000, new("id", "model", 1000, null, 1, 2, 0, 1)));
    }
    [Theory]
    [InlineData(Permission.Auto)]
    [InlineData(Permission.Notify)]
    [InlineData(Permission.Allow)]
    public void IrreversibleActionsAlwaysAsk(Permission permission) => Assert.Equal(Permission.Ask, ToolDispatcher.Resolve(Risk.AlwaysConfirm, permission));
}
