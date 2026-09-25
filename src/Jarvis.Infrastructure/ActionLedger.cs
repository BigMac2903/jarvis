using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;

public sealed class ActionLedger(Database db, Vault vault) : IActionLedger
{
    private static JsonNode? Canonical(JsonNode? value) => value switch {
        JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string,JsonNode?>(x.Key,Canonical(x.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()), _ => value?.DeepClone()
    };
    private static string Key(string task, string tool, JsonObject args) => Crypto.Hash(task + ":" + tool + ":" + Canonical(args)!.ToJsonString());
    public async Task<ToolResult?> FindAsync(string owner, string task, string tool, JsonObject args, CancellationToken ct)
    {
        var key = Key(task, tool, args); var doc = await db.GetAsync(owner, "action-ledger", key, ct);
        if (doc is null) return null;
        if (doc.Data["cipher"] is not JsonNode cipher) throw new JarvisException("Für diese Aufgabe wurde die Aktion bereits begonnen; Ergebnis unbekannt. Vor erneutem Versuch manuell prüfen.", 409);
        return JsonSerializer.Deserialize<ToolResult>(vault.Decrypt(cipher.GetValue<string>(), owner + ":action:" + key));
    }
    public async Task ClaimAsync(string owner, string task, string tool, JsonObject args, CancellationToken ct)
    {
        var key = Key(task, tool, args);
        if (await db.ExecuteAsync("INSERT INTO documents(owner,kind,id,data) VALUES($1,'action-ledger',$2,'{\"status\":\"started\"}'::jsonb) ON CONFLICT DO NOTHING", ct, owner, key) != 1)
            throw new JarvisException("Aktion bereits in Bearbeitung oder abgeschlossen.", 409);
    }
    public Task CompleteAsync(string owner, string task, string tool, JsonObject args, ToolResult result, CancellationToken ct)
    {
        var key = Key(task, tool, args);
        return db.PutAsync(owner, "action-ledger", key, new() { ["status"] = "completed", ["cipher"] = vault.Encrypt(JsonSerializer.Serialize(result), owner + ":action:" + key) }, ct);
    }
}
