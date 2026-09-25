using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class Settings(IDocumentStore store, Vault vault)
{
    public static readonly HashSet<string> Sections = ["ai", "internet", "google", "microsoft", "twilio", "obsidian", "voice"];
    public async Task<JsonObject> GetAsync(string owner, string section, CancellationToken ct) =>
        (await store.GetAsync(owner, "settings", section, ct))?.Data ?? new JsonObject();
    public async Task SaveAsync(string owner, string section, JsonObject data, CancellationToken ct)
    {
        if (!Sections.Contains(section)) throw new JarvisException("Unbekannter Einstellungsbereich.");
        if (data.ToJsonString().Length > 20000) throw new JarvisException("Konfiguration zu groß.");
        var copy = data.DeepClone().AsObject();
        foreach (var key in new[] { "apiKey", "clientSecret", "authToken", "password" })
            if (copy.Remove(key, out var secret) && secret is not null && secret.GetValue<string>().Length > 0)
                await vault.PutAsync(owner, section + "." + key, secret.GetValue<string>(), ct);
        if (section == "ai" && copy["baseUrl"] is JsonNode url)
        {
            if (!Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new JarvisException("Ungültige Provider-URL.");
            if (uri.Scheme == "http" && uri.Host != "ollama" && !uri.IsLoopback) throw new JarvisException("Externe KI-Provider benötigen HTTPS.");
        }
        await store.PutAsync(owner, "settings", section, copy, ct);
    }
    public async Task RequireAsync(string owner, string section, CancellationToken ct)
    {
        if ((await GetAsync(owner, section, ct))["enabled"]?.GetValue<bool>() != true) throw new JarvisException(section + " ist deaktiviert.", 409);
    }
}
