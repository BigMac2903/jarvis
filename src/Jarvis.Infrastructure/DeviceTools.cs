using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class DeviceTools(Database db) : IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions { get; } = Build();
    private static List<ToolDefinition> Build()
    {
        var tools = new List<ToolDefinition> {
            new("Device.List","Gepaarte Geräte auflisten.",Risk.Safe,new()),
            new("Location.GetDeviceLocation","Zuletzt freiwillig übermittelten Gerätestandort lesen.",Risk.Safe,new(){["deviceId"]=new("string","Geräte-ID")})
        };
        foreach (var command in new[] {"GetInfo","ListApplications","TakeScreenshot","GetElements","OpenApplication","CloseApplication","ClickElement","TypeText","PressKey","OpenFile","OpenFolder"})
        {
            var fields = new Dictionary<string,Field>{["deviceId"]=new("string","Geräte-ID")};
            if (command is "OpenApplication" or "CloseApplication") fields["application"] = new("string","Lokal erlaubte Anwendungs-ID",MaxLength:200);
            if (command is "ClickElement" or "TypeText") fields["elementId"] = new("string","ID aus GetElements",MaxLength:200);
            if (command == "TypeText") fields["text"] = new("string","Einzugebender Text",MaxLength:10000);
            if (command == "PressKey") fields["key"] = new("string","Erlaubte Taste",Choices:["ENTER","ESC","TAB","UP","DOWN","LEFT","RIGHT","CTRL+S"]);
            if (command is "OpenFile" or "OpenFolder") fields["path"] = new("string","Pfad unter lokal freigegebenem Ordner",MaxLength:1000);
            tools.Add(new("Device."+command, "Definiertes Gerätekommando: "+command,
                command is "GetInfo" or "ListApplications" or "TakeScreenshot" or "GetElements" ? Risk.Safe : Risk.Confirm, fields, 70));
        }
        return tools;
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject a, CancellationToken ct)
    {
        if (name == "Device.List") return new JsonArray((await db.ListAsync(actor.UserId,"devices",100,ct)).Select(d=>(JsonNode?)new JsonObject{["id"]=d.Id,["data"]=d.Data.DeepClone()}).ToArray());
        var device = a["deviceId"]!.GetValue<string>();
        var doc = await db.GetAsync(actor.UserId,"devices",device,ct) ?? throw new JarvisException("Gerät nicht gefunden.",404);
        if (name == "Location.GetDeviceLocation") return doc.Data["location"]?.DeepClone() ?? new JsonObject { ["available"] = false };
        if (doc.Data["revoked"]?.GetValue<bool>() == true) throw new JarvisException("Gerät widerrufen.",403);
        var id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("INSERT INTO commands(id,owner,device_id,command,args) VALUES($1,$2,$3,$4,$5::jsonb)",ct,id,actor.UserId,device,name[7..],a.ToJsonString());
        while (!ct.IsCancellationRequested)
        {
            var result = (await db.QueryAsync("SELECT state,result::text FROM commands WHERE id=$1 AND owner=$2",ct,id,actor.UserId))[0];
            if (result["state"]!.GetValue<string>() == "completed") return JsonNode.Parse(result["result"]!.GetValue<string>());
            if (result["state"]!.GetValue<string>() == "failed") throw new JarvisException("Gerätekommando fehlgeschlagen. Lokale Freigaben prüfen.",502);
            await Task.Delay(500,ct);
        }
        ct.ThrowIfCancellationRequested(); return null;
    }
}
