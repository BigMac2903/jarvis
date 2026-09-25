using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jarvis.Domain;

public record TaskRequirements(int Complexity, int Risk, int ContextSize, int ToolCount, bool Reasoning, bool Vision, bool Realtime, bool ComputerUse, bool FreshWeb);
public record ModelChoice(string Id, string Model, int OutputTokens, string? ReasoningEffort, decimal? InputCost, decimal? OutputCost, decimal? CachedCost, int Level);
public static class ModelRouting
{
    public static TaskRequirements Classify(string text, int contextSize, int tools, bool vision)
    {
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var complex = Has("architektur|debug|widerspr|komplex|organisier|plan|vergleichen|research") ? 65 : 20;
        if (contextSize > 40000) complex = Math.Max(complex, 70);
        var risk = Has("kaufen|überweis|löschen|kündigen|passwort") ? 90 : 20;
        return new(complex, risk, contextSize, tools, complex >= 60, vision, false,
            Has("klick|bildschirm|computersteuer"), Has("aktuell|heute|preis|neueste|wetter"));
    }
    public static IReadOnlyList<ModelChoice> Select(IEnumerable<JsonObject> registry, TaskRequirements task, string mode, bool softLimit, int escalation = 0)
    {
        var minLevel = task.Complexity >= 60 ? 2 : 0;
        minLevel = Math.Min(3, minLevel + escalation);
        var budget = task.Complexity >= 60 ? 6000 : 1800;
        if (mode == "economy" || softLimit) budget = Math.Min(budget, 1600);
        var candidates = registry.Where(m => m["enabled"]?.GetValue<bool>() == true && m["validated"]?.GetValue<bool>() == true &&
            (!task.Vision || m["supports_vision"]?.GetValue<bool>() == true) && (!task.Realtime || m["supports_realtime"]?.GetValue<bool>() == true) &&
            (task.ToolCount == 0 || m["supports_tools"]?.GetValue<bool>() == true) &&
            (m["context_window"]?.GetValue<int>() ?? 0) >= task.ContextSize + budget &&
            (m["capability_level"]?.GetValue<int>() ?? 0) >= minLevel &&
            // Frontier is never a routine default, including Maximum Quality.
            ((m["capability_level"]?.GetValue<int>() ?? 0) < 3 || escalation > 0 || task.Complexity >= 85)).ToArray();
        return candidates.Select(m => {
            var supportsReasoning = m["supports_reasoning"]?.GetValue<bool>() == true;
            var effort = supportsReasoning ? task.Complexity >= 60 ? "medium" : "low" : null;
            var allowed = PhonePolicy.Strings(m["reasoning_efforts"]);
            if (effort is not null && !allowed.Contains(effort)) effort = allowed.FirstOrDefault();
            return new ModelChoice(m["id"]!.GetValue<string>(), m["model_name"]!.GetValue<string>(), budget, effort,
                m["input_cost"]?.GetValue<decimal>(), m["output_cost"]?.GetValue<decimal>(), m["cached_input_cost"]?.GetValue<decimal>(), m["capability_level"]?.GetValue<int>() ?? 0);
        }).OrderBy(m => mode == "maximum_quality" && !softLimit ? -m.Level : 0)
          .ThenBy(m => (m.InputCost ?? 1000000) * task.ContextSize + (m.OutputCost ?? 1000000) * budget).Take(3).ToArray();
    }
    public static decimal Cost(long input, long cached, long output, ModelChoice model) =>
        (Math.Max(0, input - cached) * (model.InputCost ?? 0) + Math.Clamp(cached, 0, input) * (model.CachedCost ?? model.InputCost ?? 0) + Math.Max(0, output) * (model.OutputCost ?? 0)) / 1_000_000m;
}
