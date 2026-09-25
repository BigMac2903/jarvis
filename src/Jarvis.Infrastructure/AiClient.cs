using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public record AiTurn(string Text, JsonArray Output, IReadOnlyList<AiCall> Calls);
public record AiCall(string Id, string Name, JsonObject Arguments);
public sealed partial class AiClient(IHttpClientFactory factory, Settings settings, Vault vault)
{
    public async Task<(HttpClient Client, JsonObject Config)> ClientAsync(string owner, CancellationToken ct)
    {
        await settings.RequireAsync(owner, "ai", ct);
        var cfg = await settings.GetAsync(owner, "ai", ct);
        var client = factory.CreateClient("provider");
        client.BaseAddress = new Uri((cfg["baseUrl"]?.GetValue<string>() ?? "https://api.openai.com/v1").TrimEnd('/') + "/");
        var key = await vault.GetAsync(owner, "ai.apiKey", ct);
        if (!string.IsNullOrEmpty(key)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return (client, cfg);
    }
    public async Task<AiTurn> TurnAsync(string owner, string instructions, JsonArray input, IReadOnlyList<ToolDefinition> tools, CancellationToken ct, bool vision = false)
    {
        var (client, cfg) = await ClientAsync(owner, ct); using var dispose = client;
        var model = cfg[vision ? "visionModel" : "model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(model)) throw new JarvisException("Bitte zuerst ein KI-Modell konfigurieren.", 409);
        var compatible = cfg["protocol"]?.GetValue<string>() == "chat";
        JsonObject payload;
        if (compatible)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = instructions } };
            foreach (var item in input)
            {
                if (item?["type"]?.GetValue<string>() == "function_call")
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject {
                        ["id"] = item["call_id"]!.DeepClone(), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = item["name"]!.DeepClone(), ["arguments"] = item["arguments"]!.DeepClone() } }) });
                else if (item?["type"]?.GetValue<string>() == "function_call_output")
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = item["call_id"]!.DeepClone(), ["content"] = item["output"]!.DeepClone() });
                else if (item?["role"] is not null) messages.Add(item.DeepClone());
            }
            payload = new JsonObject { ["model"] = model, ["messages"] = messages };
            if (tools.Count > 0) payload["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject { ["type"] = "function", ["function"] = Function(t) }).ToArray());
        }
        else
        {
            payload = new JsonObject { ["model"] = model, ["instructions"] = instructions, ["input"] = input.DeepClone(), ["store"] = false, ["max_output_tokens"] = 6000 };
            if (tools.Count > 0) payload["tools"] = new JsonArray(tools.Select(t => { var f = Function(t); f["type"] = "function"; return (JsonNode?)f; }).ToArray());
        }
        using var response = await client.PostAsJsonAsync(compatible ? "chat/completions" : "responses", payload, ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException($"KI-Provider meldet HTTP {(int)response.StatusCode}. Konfiguration und Kontingent prüfen.", 502);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new JarvisException("Leere KI-Antwort.", 502);
        var output = new JsonArray(); var calls = new List<AiCall>(); var text = "";
        if (compatible)
        {
            var message = body["choices"]?[0]?["message"];
            text = message?["content"]?.GetValue<string>() ?? "";
            if (text.Length > 0) output.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
            foreach (var call in message?["tool_calls"]?.AsArray() ?? [])
            {
                var function = call!["function"]!; var id = call["id"]!.GetValue<string>();
                var name = function["name"]!.GetValue<string>(); var arguments = function["arguments"]!.GetValue<string>();
                output.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = arguments });
                calls.Add(new(id, name.Replace("__", "."), JsonNode.Parse(arguments)!.AsObject()));
            }
        }
        else
        {
            output = body["output"]?.AsArray() ?? [];
            foreach (var item in output)
            {
                if (item?["type"]?.GetValue<string>() == "function_call")
                    calls.Add(new(item["call_id"]!.GetValue<string>(), item["name"]!.GetValue<string>().Replace("__", "."), JsonNode.Parse(item["arguments"]!.GetValue<string>())!.AsObject()));
                if (item?["type"]?.GetValue<string>() == "message")
                    foreach (var content in item["content"]!.AsArray()) if (content?["type"]?.GetValue<string>() == "output_text") text += content["text"]!.GetValue<string>();
            }
        }
        return new(text, output, calls);
    }
    private static JsonObject Function(ToolDefinition t)
    {
        var schema = t.Schema;
        foreach (var field in schema["properties"]!.AsObject()) foreach (var key in field.Value!.AsObject().Where(p => p.Value is null).Select(p => p.Key).ToArray()) field.Value.AsObject().Remove(key);
        return new JsonObject { ["name"] = t.Name.Replace(".", "__"), ["description"] = t.Description, ["parameters"] = schema, ["strict"] = false };
    }
    public async Task<string> TextAsync(string owner, string instruction, string prompt, CancellationToken ct) =>
        (await TurnAsync(owner, instruction, new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }), [], ct)).Text;
    public async Task TestAsync(string owner, CancellationToken ct)
    {
        var (client, _) = await ClientAsync(owner, ct); using var dispose = client;
        using var r = await client.GetAsync("models", ct);
        if (!r.IsSuccessStatusCode) throw new JarvisException($"KI-Verbindung: HTTP {(int)r.StatusCode}", 502);
    }
}
