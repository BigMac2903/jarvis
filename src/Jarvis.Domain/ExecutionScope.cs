using System.Text.Json.Nodes;
namespace Jarvis.Domain;

public sealed class ExecutionScope : IDisposable
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string? TaskId => Current.Value;
    private readonly string? previous;
    public ExecutionScope(string? taskId) { previous = Current.Value; Current.Value = taskId; }
    public void Dispose() => Current.Value = previous;
}
public interface IActionLedger
{
    Task<ToolResult?> FindAsync(string owner, string task, string tool, JsonObject args, CancellationToken ct);
    Task ClaimAsync(string owner, string task, string tool, JsonObject args, CancellationToken ct);
    Task CompleteAsync(string owner, string task, string tool, JsonObject args, ToolResult result, CancellationToken ct);
}
