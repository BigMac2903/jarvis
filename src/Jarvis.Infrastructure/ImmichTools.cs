using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class ImmichTools(ConnectorHttp http, Settings settings) : IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Immich.Search", "Fotos/Videos mit Immich-Smart-Suche und vorhandenen Indizes finden.", Risk.Safe, new() {
            ["query"] = new("string", "Natürliche Suchfrage", MaxLength: 500), ["takenAfter"] = new("string", "ISO-Datum", false, 50),
            ["takenBefore"] = new("string", "ISO-Datum", false, 50), ["isFavorite"] = new("boolean", "Nur Favoriten", false), ["type"] = new("string", "IMAGE oder VIDEO", false, Choices: ["IMAGE", "VIDEO"])
        }),
        new("Immich.Albums", "Alben in Immich anzeigen.", Risk.Safe, new()),
        new("Immich.Asset", "Metadaten eines Assets lesen.", Risk.Safe, new() { ["id"] = new("string", "Asset-UUID", MaxLength: 40) }),
        new("Immich.Preview", "Vorschau für freigegebene Vision-Analyse laden. Original bleibt unverändert.", Risk.Confirm, new() { ["id"] = new("string", "Asset-UUID", MaxLength: 40) })
    ];
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct)
    {
        string path; HttpMethod method = HttpMethod.Get; HttpContent? body = null;
        if (name == "Immich.Search") {
            var query = args.DeepClone().AsObject(); query["size"] = 40; query["withExif"] = true;
            path = "api/search/smart"; method = HttpMethod.Post; body = JsonContent.Create(query);
        } else if (name == "Immich.Albums") path = "api/albums";
        else {
            if (!Guid.TryParse(args["id"]?.GetValue<string>(), out var id)) throw new JarvisException("Asset-ID ungültig.");
            path = "api/assets/" + id;
            if (name == "Immich.Preview") {
                if ((await settings.GetAsync(actor.UserId, "immich", ct))["allowVision"]?.GetValue<bool>() != true) throw new JarvisException("Vision-Übertragung für Immich ist deaktiviert.", 403);
                path += "/thumbnail?size=preview";
            }
        }
        var response = await http.RequestAsync(actor.UserId, "immich", method, path, body, null, ct, 5_000_000);
        if (name == "Immich.Preview") {
            if (response.Mime is not ("image/jpeg" or "image/png" or "image/webp")) throw new JarvisException("Keine sichere Bildvorschau.", 415);
            return new JsonObject { ["image"] = "data:" + response.Mime + ";base64," + Convert.ToBase64String(response.Bytes), ["untrusted"] = true };
        }
        return JsonNode.Parse(response.Bytes);
    }
    public async Task TestAsync(string owner, CancellationToken ct) => _ = await http.RequestAsync(owner, "immich", HttpMethod.Get, "api/users/me", null, null, ct);
}
