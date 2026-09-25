using System.Text.Json.Nodes;
namespace Jarvis.Domain;
public enum Risk { Safe, Confirm, AlwaysConfirm }
public enum Permission { Allow, Ask, Deny, Auto, Notify, AlwaysConfirm }
public record Actor(string UserId, string? DeviceId = null);
public record Field(string Type, string Description, bool Required = true, int MaxLength = 10000, string[]? Choices = null);
public record ToolDefinition(string Name, string Description, Risk Risk, Dictionary<string, Field> Fields, int TimeoutSeconds = 30)
{
    public JsonObject Schema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject(Fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Key, new JsonObject {
            ["type"] = f.Value.Type, ["description"] = f.Value.Description,
            ["maxLength"] = f.Value.Type == "string" ? JsonValue.Create(f.Value.MaxLength) : null,
            ["enum"] = f.Value.Choices is null ? null : new JsonArray(f.Value.Choices.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        }))),
        ["required"] = new JsonArray(Fields.Where(f => f.Value.Required).Select(f => (JsonNode?)JsonValue.Create(f.Key)).ToArray())
    };
}
public record ToolResult(string Status, JsonNode? Data = null, string? ApprovalId = null);
public record StoredDocument(string Id, string Owner, string Kind, JsonObject Data, DateTimeOffset UpdatedAt);
public record SearchHit(string Title, string Url, string Snippet, string? PublishedAt = null);
public interface IWebSearchProvider
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> NewsSearchAsync(string query, int count, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> ImageSearchAsync(string query, int count, CancellationToken ct);
}
public interface IDocumentStore
{
    Task<StoredDocument?> GetAsync(string owner, string kind, string id, CancellationToken ct);
    Task<IReadOnlyList<StoredDocument>> ListAsync(string owner, string kind, int limit, CancellationToken ct);
    Task PutAsync(string owner, string kind, string id, JsonObject data, CancellationToken ct);
    Task DeleteAsync(string owner, string kind, string? id, CancellationToken ct);
}
public interface IToolHandler
{
    IReadOnlyList<ToolDefinition> Definitions { get; }
    Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct);
}
public interface IAuthorizationStore
{
    Task<Permission?> GetPermissionAsync(string owner, string tool, CancellationToken ct);
    Task<string> RequestAsync(string owner, string tool, JsonObject args, CancellationToken ct);
    Task<bool> ConsumeAsync(string owner, string id, string tool, JsonObject args, CancellationToken ct);
    Task AuditAsync(string owner, string tool, string status, long milliseconds, string? approval, JsonObject args, CancellationToken ct);
}
public interface IEventSink { Task SendAsync(string owner, string topic, object data, CancellationToken ct); }
public interface IAutonomyPolicy { Task<Permission?> ResolveAsync(string owner, ToolDefinition tool, Permission? permission, CancellationToken ct); }
public class JarvisException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
