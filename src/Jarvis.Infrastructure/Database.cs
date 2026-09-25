using System.Reflection;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Npgsql;
namespace Jarvis.Infrastructure;
public sealed class Database(NpgsqlDataSource source) : IDocumentStore
{
    public NpgsqlDataSource Source => source;
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(826415); CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".sql")).Order())
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=$1)", connection, tx);
            exists.Parameters.AddWithValue(resource);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            await using var migration = new NpgsqlCommand(await reader.ReadToEndAsync(ct), connection, tx);
            await migration.ExecuteNonQueryAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES($1)", connection, tx);
            insert.Parameters.AddWithValue(resource); await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var value in values) cmd.Parameters.AddWithValue(value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<List<JsonObject>> QueryAsync(string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var value in values) cmd.Parameters.AddWithValue(value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<JsonObject>();
        while (await reader.ReadAsync(ct))
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : System.Text.Json.JsonSerializer.SerializeToNode(reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }
    public async Task<StoredDocument?> GetAsync(string owner, string kind, string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT id,owner,kind,data::text,updated_at FROM documents WHERE owner=$1 AND kind=$2 AND id=$3", ct, owner, kind, id);
        return rows.Count == 0 ? null : Parse(rows[0]);
    }
    public async Task<IReadOnlyList<StoredDocument>> ListAsync(string owner, string kind, int limit, CancellationToken ct) =>
        (await QueryAsync("SELECT id,owner,kind,data::text,updated_at FROM documents WHERE owner=$1 AND kind=$2 ORDER BY updated_at DESC LIMIT $3", ct, owner, kind, Math.Clamp(limit, 1, 1000))).Select(Parse).ToArray();
    public async Task PutAsync(string owner, string kind, string id, JsonObject data, CancellationToken ct) =>
        await ExecuteAsync("INSERT INTO documents(owner,kind,id,data) VALUES($1,$2,$3,$4::jsonb) ON CONFLICT(owner,kind,id) DO UPDATE SET data=excluded.data,updated_at=now()", ct, owner, kind, id, data.ToJsonString());
    public async Task DeleteAsync(string owner, string kind, string? id, CancellationToken ct)
    {
        if (id is null) await ExecuteAsync("DELETE FROM documents WHERE owner=$1 AND kind=$2", ct, owner, kind);
        else await ExecuteAsync("DELETE FROM documents WHERE owner=$1 AND kind=$2 AND id=$3", ct, owner, kind, id);
    }
    private static StoredDocument Parse(JsonObject r) => new(r["id"]!.GetValue<string>(), r["owner"]!.GetValue<string>(), r["kind"]!.GetValue<string>(), JsonNode.Parse(r["data"]!.GetValue<string>())!.AsObject(), r["updated_at"]!.GetValue<DateTime>());
}
