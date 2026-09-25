using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public sealed class MemoryTools(IDocumentStore store, Settings settings, IConfiguration config, SemanticMemory semantic) : IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Memory.Index", "Semantischen Index aktualisieren. Überträgt freigegebene Texte an den konfigurierten Embedding-Provider.", Risk.Confirm, new() { ["maxFiles"] = new("integer", "Maximale Anzahl Dateien (1–500)", false) }, 600),
        new("Memory.Search", "Interne Erinnerungen und freigegebenen Obsidian-Vault durchsuchen.", Risk.Safe, new() { ["query"] = new("string", "Suchbegriff", MaxLength: 500) }),
        new("Memory.Write", "Eine langfristige Erinnerung speichern.", Risk.Confirm, new() { ["title"] = new("string", "Titel", MaxLength: 200), ["text"] = new("string", "Inhalt", MaxLength: 50000) }),
        new("File.Read", "Markdown-Datei im Obsidian-Vault lesen.", Risk.Safe, new() { ["path"] = new("string", "Relativer Markdown-Pfad", MaxLength: 400) }),
        new("File.Write", "Markdown-Datei im Obsidian-Vault schreiben. Überschreibt den angegebenen Pfad.", Risk.Confirm, new() { ["path"] = new("string", "Relativer Markdown-Pfad", MaxLength: 400), ["text"] = new("string", "Markdown einschließlich optionalem Frontmatter", MaxLength: 100000) })
    ];
    public static string SafePath(string root, string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(x => x is ".." or "." or "")) throw new JarvisException("Ungültiger Vault-Pfad.");
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new JarvisException("Nur Markdown-Dateien erlaubt.");
        root = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new JarvisException("Pfad außerhalb des Vaults.");
        var current = root;
        if (Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is not null) throw new JarvisException("Vault darf kein symbolischer Link sein.");
        foreach (var part in path.Split('/')) {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new JarvisException("Symbolische Links sind nicht erlaubt.");
        }
        return full;
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject a, CancellationToken ct)
    {
        var owner = actor.UserId;
        if(name=="Memory.Index")return await semantic.IndexAsync(owner,Math.Clamp(a["maxFiles"]?.GetValue<int>()??100,1,500),ct);
        if (name == "Memory.Write") { var id = Guid.NewGuid().ToString("N"); await store.PutAsync(owner, "memory", id, a, ct); return new JsonObject { ["id"] = id }; }
        var cfg = await settings.GetAsync(owner, "obsidian", ct);
        var root = config["OBSIDIAN_PATH"] ?? "/data/obsidian";
        if (name == "Memory.Search")
        {
            var q = a["query"]!.GetValue<string>();
            var matches = new JsonArray((await store.ListAsync(owner, "memory", 1000, ct)).Where(d => d.Data.ToJsonString().Contains(q, StringComparison.OrdinalIgnoreCase)).Take(30).Select(d => (JsonNode?)new JsonObject { ["id"] = d.Id, ["data"] = d.Data.DeepClone() }).ToArray());
            foreach(var match in await semantic.SearchAsync(owner,q,ct))if(match is not null)matches.Add(match.DeepClone());
            if (cfg["enabled"]?.GetValue<bool>() == true && Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root, "*.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(3000))
                {
                    ct.ThrowIfCancellationRequested();
                    if (new FileInfo(file).Length > 1000000) continue;
                    var text = await File.ReadAllTextAsync(file, ct);
                    if (text.Contains(q, StringComparison.OrdinalIgnoreCase)) matches.Add(new JsonObject { ["path"] = Path.GetRelativePath(root, file), ["text"] = text[..Math.Min(5000, text.Length)] });
                    if (matches.Count >= 40) break;
                }
            return matches;
        }
        await settings.RequireAsync(owner, "obsidian", ct);
        var path = SafePath(root, a["path"]!.GetValue<string>());
        if (name == "File.Read")
        {
            if (!File.Exists(path)) throw new JarvisException("Datei nicht gefunden.", 404);
            if (new FileInfo(path).Length > 1000000) throw new JarvisException("Datei zu groß.");
            var text = await File.ReadAllTextAsync(path, ct);
            return new JsonObject { ["text"] = text, ["tags"] = new JsonArray(Regex.Matches(text, @"(?<!\w)#([\w/-]+)").Select(m => (JsonNode?)JsonValue.Create(m.Groups[1].Value)).DistinctBy(x => x!.ToString()).ToArray()), ["links"] = new JsonArray(Regex.Matches(text, @"\[\[([^\]]+)\]\]").Select(m => (JsonNode?)JsonValue.Create(m.Groups[1].Value)).ToArray()) };
        }
        if (cfg["writable"]?.GetValue<bool>() != true) throw new JarvisException("Vault-Schreibzugriff ist deaktiviert.", 403);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, a["text"]!.GetValue<string>(), ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new JsonObject { ["path"] = a["path"]!.DeepClone(), ["saved"] = true };
    }
}
