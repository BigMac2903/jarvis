using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class NotificationTools(IDocumentStore store,IEventSink events):IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions{get;}=[new("Notification.Send","Benachrichtigung im eigenen Dashboard anzeigen.",Risk.Safe,new(){["title"]=new("string","Titel",MaxLength:200),["message"]=new("string","Nachricht",MaxLength:4000)})];
    public async Task<JsonNode?> ExecuteAsync(Actor actor,string name,JsonObject args,CancellationToken ct)
    {
        var id=Guid.NewGuid().ToString("N");await store.PutAsync(actor.UserId,"notifications",id,args,ct);
        await events.SendAsync(actor.UserId,"notification",new{id,title=args["title"]!.GetValue<string>(),message=args["message"]!.GetValue<string>()},ct);
        return new JsonObject{["id"]=id,["deliveredToDashboard"]=true};
    }
}
