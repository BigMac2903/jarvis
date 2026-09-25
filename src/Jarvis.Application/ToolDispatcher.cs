using System.Diagnostics;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Application;
public sealed class ToolDispatcher(IEnumerable<IToolHandler> handlers, IAuthorizationStore auth, IAutonomyPolicy? autonomy = null, IEventSink? events = null, IActionLedger? ledger = null)
{
    private readonly Dictionary<string, (ToolDefinition Definition, IToolHandler Handler)> tools =
        handlers.SelectMany(h => h.Definitions.Select(d => (d, h))).ToDictionary(x => x.d.Name, x => (x.d, x.h));
    public IReadOnlyList<ToolDefinition> Definitions => tools.Values.Select(x => x.Definition).ToArray();
    public static Permission Resolve(Risk risk, Permission? configured) =>
        configured == Permission.Deny ? Permission.Deny : risk == Risk.AlwaysConfirm ? Permission.Ask :
        configured == Permission.AlwaysConfirm ? Permission.Ask : configured == Permission.Auto ? Permission.Allow : configured ?? (risk == Risk.Safe ? Permission.Allow : Permission.Ask);
    public static void Validate(ToolDefinition tool, JsonObject args)
    {
        if (args.Count > tool.Fields.Count) throw new JarvisException("Unbekannte Parameter.");
        foreach (var (key, value) in args)
        {
            if (!tool.Fields.TryGetValue(key, out var field)) throw new JarvisException($"Unbekannter Parameter: {key}");
            if (value is null) { if (field.Required) throw new JarvisException($"Parameter fehlt: {key}"); continue; }
            var valid = field.Type switch {
                "string" => value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= field.MaxLength && (!field.Required || !string.IsNullOrWhiteSpace(s)) && (field.Choices is null || field.Choices.Contains(s)),
                "integer" => value is JsonValue n && n.TryGetValue<int>(out _),
                "number" => value is JsonValue f && f.TryGetValue<double>(out var number) && double.IsFinite(number),
                "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
                "object" => value is JsonObject,
                "array" => value is JsonArray a && a.Count <= 100,
                _ => false
            };
            if (!valid) throw new JarvisException($"Ungültiger Parameter: {key}");
        }
        foreach (var f in tool.Fields.Where(x => x.Value.Required))
            if (!args.ContainsKey(f.Key) || args[f.Key] is null) throw new JarvisException($"Parameter fehlt: {f.Key}");
    }
    public async Task<ToolResult> ExecuteAsync(Actor actor, string name, JsonObject args, string? approval, CancellationToken ct)
    {
        if (actor.DeviceId is not null) throw new JarvisException("Geräte dürfen keine Agent-Tools aufrufen.", 403);
        if (!tools.TryGetValue(name, out var tool)) throw new JarvisException("Unbekanntes Tool.", 404);
        var timer = Stopwatch.StartNew(); var status = "error";
        try
        {
            Validate(tool.Definition, args);
            var configured = await auth.GetPermissionAsync(actor.UserId, name, ct);
            if(autonomy is not null)configured = await autonomy.ResolveAsync(actor.UserId,tool.Definition,configured,ct);
            var permission = Resolve(tool.Definition.Risk, configured);
            if (permission == Permission.Deny) throw new JarvisException("Aktion gesperrt.", 403);
            var taskId = tool.Definition.Risk != Risk.Safe ? ExecutionScope.TaskId : null;
            if(taskId is not null && ledger is not null && await ledger.FindAsync(actor.UserId,taskId,name,args,ct) is ToolResult recorded){status="replayed";return recorded;}
            if (permission == Permission.Ask && (approval is null || !await auth.ConsumeAsync(actor.UserId, approval, name, args, ct)))
            {
                status = "approval_required";
                return new(status, ApprovalId: await auth.RequestAsync(actor.UserId, name, args, ct));
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(tool.Definition.TimeoutSeconds));
            if(taskId is not null && ledger is not null)await ledger.ClaimAsync(actor.UserId,taskId,name,args,ct);
            var data = await tool.Handler.ExecuteAsync(actor, name, args, timeout.Token);
            if(taskId is not null && ledger is not null)await ledger.CompleteAsync(actor.UserId,taskId,name,args,new("success",data),CancellationToken.None);
            if(permission == Permission.Notify && events is not null)await events.SendAsync(actor.UserId,"activity",new{tool=name,status="completed"},ct);
            status = "success"; return new(status, data);
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (JarvisException e) { status = "error:" + e.Status; throw; }
        catch (Exception e) { status = "error:" + e.GetType().Name; throw; }
        finally { await auth.AuditAsync(actor.UserId, name, status, timer.ElapsedMilliseconds, approval, args, CancellationToken.None); }
    }
}
