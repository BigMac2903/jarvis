using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class AuthorizationStore(Database db) : IAuthorizationStore
{
    public async Task<Permission?> GetPermissionAsync(string owner, string tool, CancellationToken ct)
    {
        var doc = await db.GetAsync(owner, "permissions", tool, ct);
        if (doc is null) return null;
        if (doc.Data["expiresAt"] is JsonNode expiry && DateTimeOffset.Parse(expiry.GetValue<string>()) < DateTimeOffset.UtcNow) return null;
        return Enum.TryParse<Permission>(doc.Data["permission"]?.GetValue<string>(), out var p) ? p : null;
    }
    public async Task<string> RequestAsync(string owner, string tool, JsonObject args, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("INSERT INTO approvals(id,owner,tool,args,task_id) VALUES($1,$2,$3,$4::jsonb,$5)", ct, id, owner, tool, args.ToJsonString(),(object?)ExecutionScope.TaskId??DBNull.Value); return id;
    }
    public async Task<bool> ConsumeAsync(string owner, string id, string tool, JsonObject args, CancellationToken ct) =>
        await db.ExecuteAsync("UPDATE approvals SET state='consumed' WHERE id=$1 AND owner=$2 AND tool=$3 AND args=$4::jsonb AND state='approved' AND expires_at>now()", ct, id, owner, tool, args.ToJsonString()) == 1;
    public async Task AuditAsync(string owner, string tool, string status, long milliseconds, string? approval, JsonObject args, CancellationToken ct)
    {
        var redacted = new JsonObject { ["fields"] = new JsonArray(args.Select(x => (JsonNode?)JsonValue.Create(x.Key)).ToArray()), ["sha256"] = Crypto.Hash(args.ToJsonString()) };
        await db.ExecuteAsync("INSERT INTO audit(owner,tool,status,duration_ms,approval,parameters) VALUES($1,$2,$3,$4,$5,$6::jsonb)", ct, owner, tool, status, milliseconds, (object?)approval ?? DBNull.Value, redacted.ToJsonString());
    }
}
