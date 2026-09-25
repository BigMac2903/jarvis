using System.Text.Json.Nodes;
using Cronos;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Jarvis.Agent;
public sealed class AutomationWorker(Database db,ToolDispatcher tools,IEventSink events,OAuthService oauth,ILogger<AutomationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        while(await timer.WaitForNextTickAsync(ct))
        {
            try{
                var rows=await db.QueryAsync("SELECT owner,id,data::text FROM documents WHERE kind='automations' AND data->>'enabled'='true' ORDER BY updated_at LIMIT 500",ct);
                foreach(var row in rows){
                    var owner=row["owner"]!.GetValue<string>();var id=row["id"]!.GetValue<string>();var raw=row["data"]!.GetValue<string>();var rule=JsonNode.Parse(raw)!.AsObject();
                    try{
                        var trigger=rule["trigger"]?.GetValue<string>();
                        var due=false;var stamp="";
                        if(trigger is "time" or "cron"){
                            if(!DateTimeOffset.TryParse(rule["nextRun"]?.GetValue<string>(),out var next)){
                                if(trigger!="cron")continue;
                                next=Next(rule,DateTimeOffset.UtcNow);
                                rule["nextRun"]=next.ToString("O");await db.PutAsync(owner,"automations",id,rule,ct);continue;
                            }
                            due=next<=DateTimeOffset.UtcNow;stamp=next.ToString("O");
                        }else if(trigger is "device" or "location" or "webhook"){
                            var entries=await db.ListAsync(owner,"device-events",100,ct);
                            var after=DateTimeOffset.TryParse(rule["lastEventAt"]?.GetValue<string>(),out var parsed)?parsed:DateTimeOffset.UtcNow.AddMinutes(-1);
                            var matching=entries.Where(e=>e.UpdatedAt>after && e.Data["name"]?.GetValue<string>()==rule["event"]?.GetValue<string>() &&
                                (rule["deviceId"] is null||rule["deviceId"]!.GetValue<string>()==e.Data["deviceId"]?.GetValue<string>())).OrderBy(e=>e.UpdatedAt).FirstOrDefault();
                            if(matching is not null){due=true;stamp=matching.Id;rule["lastEventAt"]=matching.UpdatedAt.ToString("O");}
                        }else if(trigger=="calendar"){
                            if(DateTimeOffset.TryParse(rule["lastPoll"]?.GetValue<string>(),out var polled)&&polled>DateTimeOffset.UtcNow.AddMinutes(-1))continue;
                            rule["lastPoll"]=DateTimeOffset.UtcNow.ToString("O");
                            var provider=rule["provider"]?.GetValue<string>()??"google";var now=DateTimeOffset.UtcNow;var until=now.AddMinutes(Math.Clamp(rule["minutesBefore"]?.GetValue<int>()??30,1,1440));
                            var url=provider=="google"?"calendar/v3/calendars/primary/events?singleEvents=true&maxResults=50&timeMin="+Uri.EscapeDataString(now.ToString("O"))+"&timeMax="+Uri.EscapeDataString(until.ToString("O")):
                                "me/calendarView?$top=50&startDateTime="+Uri.EscapeDataString(now.ToString("O"))+"&endDateTime="+Uri.EscapeDataString(until.ToString("O"));
                            var response=await oauth.RequestAsync(owner,provider,HttpMethod.Get,url,null,ct);
                            var candidates=response?[provider=="google"?"items":"value"]?.AsArray()??[];
                            var seen=rule["seen"]?.AsArray()??new JsonArray();
                            var candidate=candidates.FirstOrDefault(e=>e?["id"] is not null&&!seen.Any(s=>s?.GetValue<string>()==e["id"]!.GetValue<string>()));
                            if(candidate is not null){stamp=candidate["id"]!.GetValue<string>();seen.Add(stamp);rule["seen"]=new JsonArray(seen.TakeLast(100).Select(s=>s!.DeepClone()).ToArray());due=true;}
                            if(!due){await db.PutAsync(owner,"automations",id,rule,ct);continue;}
                        }
                        if(!due)continue;
                        rule["lastRun"]=DateTimeOffset.UtcNow.ToString("O");rule["lastTrigger"]=stamp;rule["lastStatus"]="running";
                        if(trigger=="cron")rule["nextRun"]=Next(rule,DateTimeOffset.UtcNow).ToString("O");
                        if(trigger=="time"){var interval=Math.Clamp(rule["intervalMinutes"]?.GetValue<int>()??0,0,525600);if(interval==0)rule["enabled"]=false;else rule["nextRun"]=DateTimeOffset.UtcNow.AddMinutes(interval).ToString("O");}
                        // Compare-and-set the entire rule: concurrent edits or another worker cannot duplicate the claim.
                        var claimed=await db.ExecuteAsync("UPDATE documents SET data=$1::jsonb,updated_at=now() WHERE owner=$2 AND kind='automations' AND id=$3 AND data=$4::jsonb",ct,rule.ToJsonString(),owner,id,raw);
                        if(claimed!=1)continue;
                        var result=await tools.ExecuteAsync(new(owner),rule["tool"]!.GetValue<string>(),rule["args"]!.AsObject(),null,ct);
                        rule["lastStatus"]=result.Status;rule["approvalId"]=result.ApprovalId;
                        await db.PutAsync(owner,"automations",id,rule,ct);
                        await events.SendAsync(owner,"automation",new{id,status=result.Status},ct);
                    }catch(Exception e)when(e is not OperationCanceledException){
                        rule["lastStatus"]="failed";rule["error"]=e is JarvisException?e.Message:"Automationskonfiguration oder Provider-Aufruf fehlgeschlagen.";rule["enabled"]=false;
                        await db.PutAsync(owner,"automations",id,rule,ct);
                    }
                }
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError("Automation cycle failed: {ErrorType}",e.GetType().Name);}
        }
    }
    public static DateTimeOffset Next(JsonObject rule,DateTimeOffset from)
    {
        var expression=CronExpression.Parse(rule["cron"]!.GetValue<string>());
        var timezone=TimeZoneInfo.FindSystemTimeZoneById(rule["timezone"]?.GetValue<string>()??"UTC");
        return expression.GetNextOccurrence(from,timezone)??throw new JarvisException("Cron hat keinen nächsten Termin.");
    }
}
