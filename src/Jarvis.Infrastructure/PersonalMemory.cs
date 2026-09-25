using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class PersonalMemory(IDocumentStore store, Vault vault) : IToolHandler
{
    public static readonly string[] Categories = ["Identity", "Preferences", "Relationships", "Work", "Projects", "Devices", "Vehicles", "Travel", "Finances", "Important Dates", "Habits", "Personal Rules", "Past Decisions", "Goals", "Other"];
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Memory.PersonalSearch", "Relevante bestätigte persönliche Erinnerungen suchen; sensible Einträge bleiben ausgeschlossen.", Risk.Safe,
            new() { ["query"] = new("string", "Suchbegriffe", MaxLength: 500) }),
        new("Memory.ReadSensitive", "Einen ausdrücklich freigegebenen sensiblen Memory-Eintrag lesen. Übertragung an das Modell benötigt Bestätigung.", Risk.AlwaysConfirm,
            new() { ["id"] = new("string", "Memory-ID", MaxLength: 100) })
    ];
    public JsonObject Reveal(string owner, string id, JsonObject stored) => JsonNode.Parse(vault.Decrypt(stored["cipher"]!.GetValue<string>(), owner + ":personal-memory:" + id))!.AsObject();
    public async Task<JsonArray> ListAsync(string owner, bool sensitive, CancellationToken ct)
    {
        var result = new JsonArray();
        foreach (var doc in await store.ListAsync(owner, "personal-memory", 1000, ct)) {
            if (!sensitive && doc.Data["sensitive"]?.GetValue<bool>() == true) continue;
            var data = Reveal(owner, doc.Id, doc.Data);
            if (DateTimeOffset.TryParse(data["expires_at"]?.GetValue<string>(), out var expiry) && expiry < DateTimeOffset.UtcNow) continue;
            data["id"] = doc.Id; result.Add(data);
        }
        return result;
    }
    public async Task SaveAsync(string owner, string id, JsonObject data, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,100}$")) throw new JarvisException("Ungültige Memory-ID.");
        var fact = data["fact"]?.GetValue<string>() ?? "";
        if (fact.Length is < 1 or > 10000 || !Categories.Contains(data["category"]?.GetValue<string>())) throw new JarvisException("Memory benötigt Kategorie und Fakt (max. 10.000 Zeichen).");
        if (Regex.IsMatch(fact, @"(?i)(password|passwort|api[_ -]?key|auth[_ -]?token)\s*[:=]")) throw new JarvisException("Zugangsdaten gehören ausschließlich in den Secret Store.");
        if (string.IsNullOrWhiteSpace(data["source"]?.GetValue<string>())) throw new JarvisException("Memory-Quelle erforderlich.");
        var copy = data.DeepClone().AsObject();
        var previous = await store.GetAsync(owner, "personal-memory", id, ct);
        if (previous is not null) await store.PutAsync(owner, "memory-history", id + "_" + Guid.NewGuid().ToString("N"), new() {
            ["memoryId"] = id, ["cipher"] = previous.Data["cipher"]!.DeepClone(), ["at"] = DateTimeOffset.UtcNow.ToString("O")
        }, ct);
        copy["created_at"] = previous is null ? DateTimeOffset.UtcNow.ToString("O") : Reveal(owner, id, previous.Data)["created_at"]?.DeepClone();
        copy["updated_at"] = DateTimeOffset.UtcNow.ToString("O"); copy["confidence"] = Math.Clamp(copy["confidence"]?.GetValue<double>() ?? 1, 0, 1);
        var sensitive = copy["sensitive"]?.GetValue<bool>() != false || copy["category"]?.GetValue<string>() is "Finances" or "Identity" or "Relationships";
        copy["sensitive"] = sensitive;
        await store.PutAsync(owner, "personal-memory", id, new() { ["cipher"] = vault.Encrypt(copy.ToJsonString(), owner + ":personal-memory:" + id), ["sensitive"] = sensitive }, ct);
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct)
    {
        if (name == "Memory.ReadSensitive") {
            var id = args["id"]!.GetValue<string>();
            var doc = await store.GetAsync(actor.UserId, "personal-memory", id, ct) ?? throw new JarvisException("Erinnerung nicht gefunden.", 404);
            return Reveal(actor.UserId, id, doc.Data);
        }
        var words = Regex.Matches(args["query"]!.GetValue<string>().ToLowerInvariant(), @"[\p{L}\d]{3,}").Select(m => m.Value).Distinct().ToArray();
        return new JsonArray((await ListAsync(actor.UserId, false, ct)).Where(m => m is not null).Select(m => (Data: m!, Score: words.Count(w => m!["fact"]!.GetValue<string>().Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(m => m.Score > 0).OrderByDescending(m => m.Score).ThenByDescending(m => m.Data["pinned"]?.GetValue<bool>() == true).Take(8)
            .Select(m => m.Data.DeepClone()).ToArray());
    }
    public static JsonArray ImportCandidates(string text, string format)
    {
        if (text.Length > 5_000_000) throw new JarvisException("Import maximal 5 MB.", 413);
        var snippets = new List<string>();
        if (format == "json") {
            void Visit(JsonNode? node) {
                if (snippets.Count >= 500) return;
                if (node is JsonObject obj) {
                    if (obj["author"]?["role"]?.GetValue<string>() == "user" && obj["content"]?["parts"] is JsonArray parts) {
                        foreach (var part in parts) if (part is JsonValue value && value.TryGetValue<string>(out var s)) snippets.Add(s);
                    }
                    else if (obj["fact"] is JsonValue fact && fact.TryGetValue<string>(out var f)) snippets.Add(f);
                    foreach (var child in obj) Visit(child.Value);
                } else if (node is JsonArray array) foreach (var child in array) Visit(child);
            }
            Visit(JsonNode.Parse(text, documentOptions: new() { MaxDepth = 64 }));
        } else snippets.AddRange(text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new JsonArray(snippets.Select(s => s.Trim()).Where(s => s.Length is >= 5 and <= 10000 && seen.Add(Regex.Replace(s, @"\s+", " "))).Take(500)
            .Select(s => (JsonNode?)new JsonObject { ["candidateId"] = Guid.NewGuid().ToString("N"), ["fact"] = s, ["category"] = "Other", ["source"] = format == "json" ? "ChatGPT/JSON import – manuell zu prüfen" : "Markdown/manual import – manuell zu prüfen", ["confidence"] = 0.5, ["sensitive"] = true, ["status"] = "review" }).ToArray());
    }
    public async Task<JsonArray> PreviewAsync(string owner, string text, string format, CancellationToken ct)
    {
        var candidates = ImportCandidates(text, format);
        var existing = await ListAsync(owner, true, ct);
        foreach (var candidate in candidates) {
            var normalized = Regex.Replace(candidate!["fact"]!.GetValue<string>().Trim(), @"\s+", " ");
            candidate["duplicate"] = existing.Any(e => string.Equals(Regex.Replace(e!["fact"]!.GetValue<string>().Trim(), @"\s+", " "), normalized, StringComparison.OrdinalIgnoreCase));
            candidate["conflictCheck"] = "manual_review_required"; // No fabricated semantic certainty.
        }
        return candidates;
    }
}
