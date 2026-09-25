using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class NextcloudTools(ConnectorHttp http, Settings settings, DocumentExtractor extractor) : IToolHandler
{
    private static readonly XNamespace Dav = "DAV:";
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Nextcloud.List", "Freigegebenen Nextcloud-Ordner und Metadaten lesen.", Risk.Safe, Fields()),
        new("Nextcloud.Search", "Nextcloud-Dateinamen suchen, ohne ganze Dateien zu übertragen.", Risk.Safe, new() { ["query"] = new("string", "Dateinamensbestandteil", MaxLength: 250) }),
        new("Nextcloud.Read", "Datei lesen: Text/Office/PDF lokal extrahieren; kein Makro wird ausgeführt.", Risk.Safe, Fields(), 120),
        new("Nextcloud.Write", "UTF-8-Datei hochladen. Bestehende Dateien werden nicht überschrieben.", Risk.Confirm, new() { ["path"] = new("string", "Relativer Pfad", MaxLength: 1000), ["text"] = new("string", "Inhalt", MaxLength: 100000) }),
        new("Nextcloud.CreateFolder", "Ordner anlegen.", Risk.Confirm, Fields()),
        new("Nextcloud.Move", "Datei verschieben oder umbenennen, ohne ein Ziel zu überschreiben.", Risk.Confirm, Destination()),
        new("Nextcloud.Copy", "Datei kopieren, ohne ein Ziel zu überschreiben.", Risk.Confirm, Destination()),
        new("Nextcloud.Delete", "Datei oder Ordner löschen. Immer explizite Bestätigung.", Risk.AlwaysConfirm, Fields()),
        new("Nextcloud.Capabilities", "OCS-Fähigkeiten des konfigurierten Servers abfragen.", Risk.Safe, new())
    ];
    private static Dictionary<string, Field> Fields() => new() { ["path"] = new("string", "Relativer Pfad; / ist freigegebener Wurzelordner", MaxLength: 1000) };
    private static Dictionary<string, Field> Destination() { var f = Fields(); f["destination"] = new("string", "Neuer relativer Pfad", MaxLength: 1000); return f; }
    public async Task<string> Relative(string owner, string path, CancellationToken ct)
    {
        var cfg = await settings.GetAsync(owner, "nextcloud", ct);
        var username = cfg["username"]?.GetValue<string>() ?? throw new JarvisException("Benutzername fehlt.");
        if (username.Contains(':') || username.Contains('/') || username.Any(char.IsControl)) throw new JarvisException("Ungültiger Nextcloud-Benutzername.");
        var root = ConnectorHttp.Path(cfg["root"]?.GetValue<string>() ?? "");
        return "remote.php/dav/files/" + Uri.EscapeDataString(username) + "/" + (root.Length == 0 ? "" : root + "/") + ConnectorHttp.Path(path);
    }
    public async Task<JsonArray> List(string owner, string path, CancellationToken ct)
    {
        var relative = await Relative(owner, path, ct);
        var xml = new XElement(Dav + "propfind", new XElement(Dav + "prop", new XElement(Dav + "displayname"), new XElement(Dav + "getetag"),
            new XElement(Dav + "getcontenttype"), new XElement(Dav + "getcontentlength"), new XElement(Dav + "getlastmodified"), new XElement(Dav + "resourcetype")));
        var response = await http.RequestAsync(owner, "nextcloud", new("PROPFIND"), relative, new StringContent(xml.ToString(), Encoding.UTF8, "application/xml"), new() { ["Depth"] = "1" }, ct);
        return ParseListing(response.Bytes, new Uri(ConnectorHttp.BaseUrl(await settings.GetAsync(owner, "nextcloud", ct)), await Relative(owner, "", ct)));
    }
    public static JsonArray ParseListing(byte[] bytes, Uri allowedRoot)
    {
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
        var doc = XDocument.Load(reader); var results = new JsonArray();
        foreach (var item in doc.Descendants(Dav + "response").Take(1000))
        {
            var href = item.Element(Dav + "href")?.Value ?? "";
            if (!Uri.TryCreate(allowedRoot, href, out var uri) || !uri.AbsoluteUri.StartsWith(allowedRoot.AbsoluteUri, StringComparison.Ordinal)) continue;
            var prop = item.Elements(Dav + "propstat").FirstOrDefault(p => p.Element(Dav + "status")?.Value.Contains(" 200 ") == true)?.Element(Dav + "prop");
            if (prop is null) continue;
            var path = Uri.UnescapeDataString(uri.AbsoluteUri[allowedRoot.AbsoluteUri.Length..]).TrimEnd('/');
            try { _ = ConnectorHttp.Path(path); } catch (JarvisException) { continue; }
            results.Add(new JsonObject { ["path"] = path, ["name"] = prop.Element(Dav + "displayname")?.Value ?? System.IO.Path.GetFileName(path),
                ["etag"] = prop.Element(Dav + "getetag")?.Value, ["mime"] = prop.Element(Dav + "getcontenttype")?.Value,
                ["size"] = long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, out var size) ? size : 0,
                ["modified"] = prop.Element(Dav + "getlastmodified")?.Value, ["folder"] = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null });
        }
        return results;
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct)
    {
        var owner = actor.UserId;
        if (name == "Nextcloud.List") return await List(owner, args["path"]!.GetValue<string>(), ct);
        if (name == "Nextcloud.Capabilities") return JsonNode.Parse((await http.RequestAsync(owner, "nextcloud", HttpMethod.Get, "ocs/v2.php/cloud/capabilities?format=json", null, new() { ["OCS-APIRequest"] = "true" }, ct)).Bytes);
        if (name == "Nextcloud.Search")
        {
            var cfg = await settings.GetAsync(owner, "nextcloud", ct);
            var root = new Uri(ConnectorHttp.BaseUrl(cfg), await Relative(owner, "", ct));
            var xml = new XElement(Dav + "searchrequest", new XElement(Dav + "basicsearch",
                new XElement(Dav + "select", new XElement(Dav + "prop", new XElement(Dav + "displayname"), new XElement(Dav + "getetag"), new XElement(Dav + "getcontenttype"), new XElement(Dav + "resourcetype"))),
                new XElement(Dav + "from", new XElement(Dav + "scope", new XElement(Dav + "href", root.AbsolutePath), new XElement(Dav + "depth", "infinity"))),
                new XElement(Dav + "where", new XElement(Dav + "like", new XElement(Dav + "prop", new XElement(Dav + "displayname")), new XElement(Dav + "literal", "%" + args["query"]!.GetValue<string>().Replace("%", "").Replace("_", "") + "%"))),
                new XElement(Dav + "limit", new XElement(Dav + "nresults", 100))));
            var result = await http.RequestAsync(owner, "nextcloud", new("SEARCH"), "remote.php/dav/", new StringContent(xml.ToString(), Encoding.UTF8, "application/xml"), null, ct);
            return ParseListing(result.Bytes, root);
        }
        var path = args["path"]!.GetValue<string>(); var relative = await Relative(owner, path, ct);
        if (name == "Nextcloud.Read") {
            var content = await http.RequestAsync(owner, "nextcloud", HttpMethod.Get, relative, null, null, ct);
            var result = await extractor.ExtractAsync(path, content.Mime, content.Bytes, ct); result["source"] = "Nextcloud"; result["path"] = path; result["etag"] = content.Etag; return result;
        }
        if ((await settings.GetAsync(owner, "nextcloud", ct))["writable"]?.GetValue<bool>() != true) throw new JarvisException("Nextcloud-Schreibzugriff deaktiviert.", 403);
        if (ConnectorHttp.Path(path).Length == 0) throw new JarvisException("Wurzeloperation nicht erlaubt.", 403);
        var headers = new Dictionary<string, string>(); HttpContent? body = null;
        var method = name switch { "Nextcloud.Write" => "PUT", "Nextcloud.CreateFolder" => "MKCOL", "Nextcloud.Move" => "MOVE", "Nextcloud.Copy" => "COPY", "Nextcloud.Delete" => "DELETE", _ => throw new JarvisException("Unbekanntes Tool.") };
        if (method is "MOVE" or "COPY") {
            var destination = args["destination"]!.GetValue<string>();
            if (ConnectorHttp.Path(destination).Length == 0) throw new JarvisException("Ungültiges Ziel.");
            headers["Destination"] = new Uri(ConnectorHttp.BaseUrl(await settings.GetAsync(owner, "nextcloud", ct)), await Relative(owner, destination, ct)).AbsoluteUri;
            headers["Overwrite"] = "F";
        }
        if (method == "PUT") { body = new StringContent(args["text"]!.GetValue<string>(), Encoding.UTF8, "text/plain"); headers["If-None-Match"] = "*"; }
        await http.RequestAsync(owner, "nextcloud", new(method), relative, body, headers, ct);
        return new JsonObject { ["success"] = true, ["path"] = path };
    }
}
