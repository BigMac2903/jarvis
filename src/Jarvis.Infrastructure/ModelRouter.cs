using System.Text.Json.Nodes;
using Jarvis.Domain;
using Npgsql;

namespace Jarvis.Infrastructure;

public sealed class ModelRouter(Database db, Settings settings)
{
    public async Task<ModelChoice> NamedAsync(string owner,string model,int outputTokens,CancellationToken ct)
    {
        var entry=(await db.ListAsync(owner,"ai-models",200,ct)).FirstOrDefault(x=>x.Data["model_name"]?.GetValue<string>()==model)?.Data;
        return new("named",model,outputTokens,null,entry?["input_cost"]?.GetValue<decimal>(),entry?["output_cost"]?.GetValue<decimal>(),entry?["cached_input_cost"]?.GetValue<decimal>(),1);
    }
    public static async Task GuardRealtimeBudgetAsync(Settings settings,string owner,CancellationToken ct)
    {
        var cfg=await settings.GetAsync(owner,"budget",ct);
        if((cfg["dailyHard"]?.GetValue<decimal>()??0)>0||(cfg["monthlyHard"]?.GetValue<decimal>()??0)>0)
            throw new JarvisException("Realtime-Audio ist bei aktivem Hard-Limit gesperrt, bis ein verlässlich begrenzter Audio-Abrechnungspfad konfiguriert ist. Textaufgaben bleiben budgetkontrolliert.",409);
    }
    public async Task<IReadOnlyList<ModelChoice>> ChooseAsync(string owner, string text, int size, int tools, bool vision, JsonObject ai, CancellationToken ct, int escalation = 0)
    {
        var budget = await settings.GetAsync(owner, "budget", ct);
        var totals = await TotalsAsync(owner, ct);
        bool soft = (budget["dailySoft"]?.GetValue<decimal>() is decimal d && d > 0 && totals.Day >= d) ||
            (budget["monthlySoft"]?.GetValue<decimal>() is decimal m && m > 0 && totals.Month >= m);
        var registry = await db.ListAsync(owner, "ai-models", 200, ct);
        if (registry.Count == 0) {
            var name = ai[vision ? "visionModel" : "model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) throw new JarvisException("KI-Modell oder validierte Model Registry konfigurieren.", 409);
            return [new("legacy", name, 4000, null, null, null, null, 1)];
        }
        var models = registry.Select(d => { var data = d.Data.DeepClone().AsObject(); data["id"] = d.Id; return data; });
        var selected = ModelRouting.Select(models, ModelRouting.Classify(text, size, tools, vision), budget["mode"]?.GetValue<string>() ?? "best_value", soft, escalation);
        if (selected.Count == 0) throw new JarvisException("Kein validiertes Modell mit passenden Fähigkeiten und Kontextfenster verfügbar.", 409);
        return selected;
    }
    public async Task<(decimal Day, decimal Month)> TotalsAsync(string owner, CancellationToken ct)
    {
        var row = (await db.QueryAsync("SELECT coalesce(sum(estimated_cost) FILTER (WHERE created_at >= date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'),0) AS day,coalesce(sum(estimated_cost),0) AS month FROM ai_usage WHERE owner=$1 AND created_at>=date_trunc('month',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'", ct, owner)).Single();
        return (row["day"]!.GetValue<decimal>(), row["month"]!.GetValue<decimal>());
    }
    public async Task<string> ReserveAsync(string owner, ModelChoice model, int inputUpperBound, string kind, CancellationToken ct)
    {
        var cfg = await settings.GetAsync(owner, "budget", ct);
        var daily = cfg["dailyHard"]?.GetValue<decimal>() ?? 0;
        var monthly = cfg["monthlyHard"]?.GetValue<decimal>() ?? 0;
        if ((daily > 0 || monthly > 0) && (model.InputCost is null || model.OutputCost is null))
            throw new JarvisException("Hard-Limit aktiv: Modellpreise müssen vor kostenpflichtigen Aufrufen konfiguriert sein.", 409);
        var reserve = ModelRouting.Cost(inputUpperBound, 0, model.OutputTokens, model);
        var id = Guid.NewGuid().ToString("N");
        await using var connection = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, tx)) { gate.Parameters.AddWithValue(owner); await gate.ExecuteNonQueryAsync(ct); }
        await using (var sum = new NpgsqlCommand("SELECT coalesce(sum(estimated_cost) FILTER (WHERE created_at>=date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'),0),coalesce(sum(estimated_cost),0) FROM ai_usage WHERE owner=$1 AND created_at>=date_trunc('month',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'", connection, tx)) {
            sum.Parameters.AddWithValue(owner); await using var reader = await sum.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            if ((daily > 0 && reader.GetDecimal(0) + reserve > daily) || (monthly > 0 && reader.GetDecimal(1) + reserve > monthly)) throw new JarvisException("KI-Hard-Limit erreicht. Anfrage vor dem Provideraufruf gestoppt.", 429);
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO ai_usage(id,owner,model,task_type,estimated_cost,status,price_known) VALUES($1,$2,$3,$4,$5,'reserved',$6)", connection, tx)) {
            insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(owner); insert.Parameters.AddWithValue(model.Model); insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue(reserve); insert.Parameters.AddWithValue(model.InputCost is not null && model.OutputCost is not null); await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if(ExecutionScope.TaskId is string taskId)await db.ExecuteAsync("UPDATE ai_usage SET task_id=$2 WHERE id=$1",ct,id,taskId);
        return id;
    }
    public async Task CompleteAsync(string id, ModelChoice model, JsonNode? usage, long milliseconds, bool success, CancellationToken ct)
    {
        long Value(string a, string b) => usage?[a]?.GetValue<long>() ?? usage?[b]?.GetValue<long>() ?? 0;
        var input = Value("input_tokens", "prompt_tokens"); var output = Value("output_tokens", "completion_tokens");
        var cached = usage?["input_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? usage?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? 0;
        var reasoning = usage?["output_tokens_details"]?["reasoning_tokens"]?.GetValue<long>() ?? usage?["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<long>() ?? 0;
        await db.ExecuteAsync("UPDATE ai_usage SET input_tokens=$2,cached_tokens=$3,output_tokens=$4,reasoning_tokens=$5,duration_ms=$6,status=$7,estimated_cost=CASE WHEN $8 THEN $9 ELSE estimated_cost END WHERE id=$1", ct,
            id, input, cached, output, reasoning, milliseconds, success ? "completed" : "failed_or_unknown", usage is not null, ModelRouting.Cost(input, cached, output, model));
    }
}
