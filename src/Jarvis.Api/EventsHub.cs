using Jarvis.Domain;
using Microsoft.AspNetCore.SignalR;
namespace Jarvis.Api;
public sealed class EventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var actor=Context.GetHttpContext()!.Actor();
        if(actor.DeviceId is not null) throw new HubException("User session required.");
        await Groups.AddToGroupAsync(Context.ConnectionId,actor.UserId);
        await base.OnConnectedAsync();
    }
}
public sealed class EventSink(IHubContext<EventsHub> hub):IEventSink
{
    public Task SendAsync(string owner,string topic,object data,CancellationToken ct)=>hub.Clients.Group(owner).SendAsync(topic,data,ct);
}
