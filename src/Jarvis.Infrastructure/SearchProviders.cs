using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class SearchProvider(IHttpClientFactory clients, Settings settings, Vault vault, string owner) : IWebSearchProvider
{
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct) => Search(query, count, "web", ct);
    public Task<IReadOnlyList<SearchHit>> NewsSearchAsync(string query, int count, CancellationToken ct) => Search(query, count, "news", ct);
    public Task<IReadOnlyList<SearchHit>> ImageSearchAsync(string query, int count, CancellationToken ct) => Search(query, count, "images", ct);
    private async Task<IReadOnlyList<SearchHit>> Search(string query, int count, string category, CancellationToken ct)
    {
        await settings.RequireAsync(owner, "internet", ct);
        var cfg = await settings.GetAsync(owner, "internet", ct);
        using var client = clients.CreateClient("provider");
        var provider = cfg["provider"]?.GetValue<string>() ?? "brave";
        var q = Uri.EscapeDataString(query); count = Math.Clamp(count, 1, 20);
        JsonObject body;
        if (provider == "brave")
        {
            var key = await vault.GetAsync(owner, "internet.apiKey", ct) ?? throw new JarvisException("Brave API-Key fehlt.", 409);
            client.DefaultRequestHeaders.Add("X-Subscription-Token", key);
            using var response = await client.GetAsync($"https://api.search.brave.com/res/v1/{category}/search?q={q}&count={count}", ct);
            if (!response.IsSuccessStatusCode) throw new JarvisException($"Suchprovider: HTTP {(int)response.StatusCode}", 502);
            body = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))!;
            return (body[category == "web" ? "web" : "results"] is JsonObject group ? group["results"]?.AsArray() : body["results"]?.AsArray() ?? [])
                ?.Where(x => x?["url"] is not null).Select(x => new SearchHit(x!["title"]?.GetValue<string>() ?? "", x["url"]!.GetValue<string>(), x["description"]?.GetValue<string>() ?? "", x["age"]?.ToString())).Take(count).ToArray() ?? [];
        }
        if (provider != "searxng") throw new JarvisException("Unbekannter Suchprovider.");
        var baseUrl = cfg["searxUrl"]?.GetValue<string>() ?? "http://jarvis-search:8080";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && baseUrl != "http://jarvis-search:8080")) throw new JarvisException("SearXNG benötigt HTTPS oder den internen jarvis-search Dienst.");
        body = await client.GetFromJsonAsync<JsonObject>($"{baseUrl.TrimEnd('/')}/search?q={q}&format=json&categories={(category == "web" ? "general" : category)}", ct) ?? new();
        return (body["results"]?.AsArray() ?? []).Select(x => new SearchHit(x!["title"]?.GetValue<string>() ?? "", x["url"]!.GetValue<string>(), x["content"]?.GetValue<string>() ?? "", x["publishedDate"]?.ToString())).Take(count).ToArray();
    }
}
