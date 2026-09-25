using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Infrastructure;

public sealed class DocumentExtractor(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<JsonObject> ExtractAsync(string path, string mime, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > 10_000_000) throw new JarvisException("Datei größer als 10 MB.", 413);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf" && bytes.AsSpan().StartsWith("%PDF-"u8)) {
            using var client = clients.CreateClient("browser");
            using var request = new HttpRequestMessage(HttpMethod.Post, (configuration["BROWSER_URL"] ?? "http://jarvis-browser:8000") + "/parse-pdf");
            request.Headers.Add("X-Service-Token", configuration["BROWSER_TOKEN"]); request.Content = new ByteArrayContent(bytes);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new JarvisException("Lokaler PDF-Parser nicht verfügbar.", 502);
            return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new JarvisException("Leeres Parsergebnis.");
        }
        string text;
        if (extension is ".docx" or ".xlsx" or ".pptx") text = OfficeText(bytes);
        else if (extension is ".txt" or ".md" or ".csv" or ".json" or ".tsv") text = Encoding.UTF8.GetString(bytes);
        else if (mime is "image/jpeg" or "image/png") return new() { ["image"] = "data:" + mime + ";base64," + Convert.ToBase64String(bytes), ["untrusted"] = true };
        else throw new JarvisException("Dateiformat wird nicht analysiert. Keine Ausführung von Dateien oder Makros.", 415);
        return new() { ["text"] = text[..Math.Min(text.Length, 100000)], ["truncated"] = text.Length > 100000, ["untrusted"] = true };
    }
    public static string OfficeText(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes));
        if (archive.Entries.Count > 2000 || archive.Entries.Sum(e => e.Length) > 30_000_000) throw new JarvisException("Office-Archiv überschreitet Extraktionslimit.", 413);
        var result = new StringBuilder();
        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml") &&
            (e.FullName.StartsWith("word/") || e.FullName.StartsWith("xl/") || e.FullName.StartsWith("ppt/slides/"))).OrderBy(e => e.FullName)) {
            if (entry.Length > 10_000_000) throw new JarvisException("Office-XML zu groß.", 413);
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
            var document = XDocument.Load(reader);
            result.AppendLine("[" + entry.FullName + "]");
            foreach (var el in document.Descendants().Where(e => e.Name.LocalName is "t" or "v")) result.Append(el.Value).Append(' ');
            result.AppendLine(); if (result.Length > 100000) break;
        }
        return result.ToString();
    }
}
