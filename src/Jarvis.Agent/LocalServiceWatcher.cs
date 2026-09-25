using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

// Optional low-rate verification of explicitly registered targets, never subnet enumeration.
public sealed class LocalServiceWatcher(Database db,Settings settings,ToolDispatcher tools,ILogger<LocalServiceWatcher> logger):BackgroundService
{
    private static readonly HashSet<int> KnownPorts=[80,443,631,1883,2283,5000,5001,8006,8080,8123,8443,8883,9100];
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(15));
        while(await timer.WaitForNextTickAsync(ct))try {
            foreach(var user in await db.QueryAsync("SELECT id FROM users",ct)){
                var owner=user["id"]!.GetValue<string>();var cfg=await settings.GetAsync(owner,"local-network",ct);
                if(cfg["enabled"]?.GetValue<bool>()!=true || cfg["discoveryEnabled"]?.GetValue<bool>()!=true || cfg["probesEnabled"]?.GetValue<bool>()!=true)continue;
                var prior=await db.ListAsync(owner,"local-discovery",100,ct);
                var services=(await db.ListAsync(owner,"local-services",100,ct))
                    .Where(d=>d.Data["enabled"]?.GetValue<bool>()==true&&d.Data["autoCheck"]?.GetValue<bool>()==true&&KnownPorts.Contains(d.Data["port"]?.GetValue<int>()??0))
                    .OrderBy(d=>prior.FirstOrDefault(p=>p.Id==d.Id)?.UpdatedAt??DateTimeOffset.MinValue).Take(16);
                foreach(var service in services){
                    var previous=prior.FirstOrDefault(p=>p.Id==service.Id);
                    // Pending, denied or expired approvals are never repeatedly re-created automatically.
                    if(previous?.Data["approvalId"] is not null)continue;
                    try {
                        using var scope=new LocalNetworkScope();
                        var result=await tools.ExecuteAsync(new(owner),"LocalNetwork.CheckPort",new(){["serviceId"]=service.Id,["revision"]=service.Data["revision"]?.DeepClone()},null,ct);
                        await db.PutAsync(owner,"local-discovery",service.Id,new(){["status"]=result.Status,["approvalId"]=result.ApprovalId,["identityVerified"]=false},ct);
                    }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception e){
                        logger.LogWarning("Registered local service check failed: {Type}",e.GetType().Name);
                        await db.PutAsync(owner,"local-discovery",service.Id,new(){["status"]="failed",["identityVerified"]=false},ct);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3),ct);
                }
            }
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        catch(Exception e){logger.LogWarning("Local service check stopped: {Type}",e.GetType().Name);}
    }
}
