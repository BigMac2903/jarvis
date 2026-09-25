using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public sealed class BrowserClient(IHttpClientFactory clients, IConfiguration config, Settings settings)
{
    public async Task<JsonNode?> CallAsync(string owner, string action, JsonObject args, CancellationToken ct)
    {
        var cfg = await settings.GetAsync(owner, "internet", ct);
        if (cfg["enabled"]?.GetValue<bool>() != true) throw new JarvisException("Internet ist deaktiviert.", 409);
        if (action == "download" && cfg["downloads"]?.GetValue<bool>() != true) throw new JarvisException("Downloads sind deaktiviert.", 403);
        if (action == "pdf" && cfg["pdf"]?.GetValue<bool>() != true) throw new JarvisException("PDF-Lesen ist deaktiviert.", 403);
        if (action is not ("fetch" or "pdf" or "download") && cfg["browser"]?.GetValue<bool>() != true) throw new JarvisException("Browser ist deaktiviert.", 403);
        using var client = clients.CreateClient("browser");
        using var request = new HttpRequestMessage(HttpMethod.Post, (config["BROWSER_URL"] ?? "http://jarvis-browser:8000") + "/action");
        request.Headers.Add("X-Service-Token", config["BROWSER_TOKEN"]);
        request.Content = JsonContent.Create(new { owner, action, args, maxBytes = Math.Clamp(cfg["maxDownloadMb"]?.GetValue<int>() ?? 10, 1, 100) * 1024 * 1024 });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException($"Browser-Aufruf abgelehnt oder fehlgeschlagen (HTTP {(int)response.StatusCode}).", 502);
        return await response.Content.ReadFromJsonAsync<JsonNode>(ct);
    }
}
