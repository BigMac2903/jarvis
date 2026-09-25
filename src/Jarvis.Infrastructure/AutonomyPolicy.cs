using Jarvis.Domain;
namespace Jarvis.Infrastructure;

public sealed class AutonomyPolicy(Settings settings) : IAutonomyPolicy
{
    public async Task<Permission?> ResolveAsync(string owner, ToolDefinition tool, Permission? permission, CancellationToken ct)
    {
        var cfg = await settings.GetAsync(owner, "autonomy", ct);
        var level = Math.Clamp(cfg["level"]?.GetValue<int>() ?? 3, 0, 3);
        if (permission == Permission.Deny) return permission;
        if (level == 0 && tool.Risk != Risk.Safe) return Permission.Deny;
        if (tool.Risk == Risk.AlwaysConfirm) return Permission.AlwaysConfirm;
        if (permission is not null) return permission;
        // Safe, reversible defaults. Medium/high-impact actions do not inherit blanket authority.
        if (level >= 2 && tool.Name is "Nextcloud.Move" or "Nextcloud.Copy" or "Nextcloud.CreateFolder" or "Memory.Write" or "Device.OpenApplication")
            return Permission.Notify;
        return null;
    }
}
