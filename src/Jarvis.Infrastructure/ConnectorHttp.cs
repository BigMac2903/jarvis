using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class ConnectorHttp(IHttpClientFactory clients, Settings settings, Vault vault)
{
    public static Uri BaseUrl(JsonObject cfg)
    {
        if (!Uri.TryCreate(cfg["url"]?.GetValue<string>(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new JarvisException("Connectoren benötigen eine ausdrücklich konfigurierte HTTPS-Basis-URL ohne Zugangsdaten oder Query.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static string Path(string path)
    {
        path = path.Trim('/');
        if (path.Length > 1000 || path.Contains('\\') || path.Contains('%') || path.Any(char.IsControl) || path.Contains(':') ||
            (path.Length > 0 && path.Split('/').Any(p => p is ".." or "." or ""))) throw new JarvisException("Unsicherer Connector-Pfad.");
        return string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }
    public async Task<(byte[] Bytes, string Mime, string? Etag)> RequestAsync(string owner, string connector, HttpMethod method,
        string relative, HttpContent? content, Dictionary<string, string>? headers, CancellationToken ct, int limit = 10_000_000)
    {
        await settings.RequireAsync(owner, connector, ct);
        var cfg = await settings.GetAsync(owner, connector, ct);
        var root = BaseUrl(cfg);
        var url = new Uri(root, relative);
        if (!url.AbsoluteUri.StartsWith(root.AbsoluteUri, StringComparison.Ordinal) || relative.StartsWith('/') || relative.Contains(".."))
            throw new JarvisException("Connector-Ziel außerhalb der Konfiguration.", 403);
        using var client = clients.CreateClient("provider"); // Redirects disabled: credentials never follow Location.
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (connector == "nextcloud")
        {
            var username = cfg["username"]?.GetValue<string>() ?? throw new JarvisException("Nextcloud-Benutzername fehlt.");
            var password = await vault.GetAsync(owner, "nextcloud.password", ct) ?? throw new JarvisException("Nextcloud-App-Passwort fehlt.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        }
        else request.Headers.Add("x-api-key", await vault.GetAsync(owner, connector + ".apiKey", ct) ?? throw new JarvisException("Connector-Key fehlt."));
        if (headers is not null) foreach (var h in headers) request.Headers.Add(h.Key, h.Value);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException($"{connector}: HTTP {(int)response.StatusCode}", 502);
        if (response.Content.Headers.ContentLength > limit) throw new JarvisException("Connector-Datei zu groß.", 413);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[32768]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (output.Length + read > limit) throw new JarvisException("Connector-Antwort zu groß.", 413); output.Write(buffer, 0, read); }
        return (output.ToArray(), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", response.Headers.ETag?.ToString());
    }
}
