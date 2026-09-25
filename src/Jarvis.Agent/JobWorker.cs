using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Jarvis.Agent;
public sealed class JobWorker(Database db, ResearchService research, IEventSink events, ILogger<JobWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string,CancellationTokenSource> running=new();
    public void Cancel(string id) { if(running.TryGetValue(id,out var ct)) ct.Cancel(); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await db.ExecuteAsync("UPDATE documents SET data=jsonb_set(data,'{status}','\"interrupted\"') WHERE kind='jobs' AND data->>'status'='running'",stoppingToken);
        while(!stoppingToken.IsCancellationRequested)
        {
            try {
                var jobs=await db.QueryAsync("SELECT owner,id,data::text FROM documents WHERE kind='jobs' AND data->>'status'='pending' ORDER BY updated_at LIMIT 1",stoppingToken);
                foreach(var row in jobs)
                {
                    var owner=row["owner"]!.GetValue<string>(); var id=row["id"]!.GetValue<string>();
                    var data=JsonNode.Parse(row["data"]!.GetValue<string>())!.AsObject();
                    data["status"]="running";
                    if(await db.ExecuteAsync("UPDATE documents SET data=$1::jsonb,updated_at=now() WHERE owner=$2 AND kind='jobs' AND id=$3 AND data->>'status'='pending'",stoppingToken,data.ToJsonString(),owner,id)!=1)continue;
                    using var linked=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); running[id]=linked;
                    try {
                        var result=await research.RunAsync(owner,id,data["question"]!.GetValue<string>(),data["mode"]!.GetValue<string>(),data["fresh"]?.GetValue<bool>()??false,linked.Token);
                        data["status"]="completed"; data["result"]=result;
                    } catch(OperationCanceledException) { data["status"]="cancelled"; }
                    catch(Exception e) { data["status"]="failed"; data["error"]=e is JarvisException ? e.Message : "Recherche fehlgeschlagen."; logger.LogWarning("Research {JobId} failed: {ErrorType}",id,e.GetType().Name); }
                    finally { running.TryRemove(id,out _); }
                    await db.PutAsync(owner,"jobs",id,data,CancellationToken.None);
                    await events.SendAsync(owner,"job",new{id,status=data["status"]!.GetValue<string>()},CancellationToken.None);
                }
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception e) { logger.LogError("Worker cycle failed: {ErrorType}",e.GetType().Name); }
            await Task.Delay(1500,stoppingToken);
        }
    }
}
