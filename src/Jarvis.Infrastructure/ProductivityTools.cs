using System.Net.Mail;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed class ProductivityTools(OAuthService oauth, IDocumentStore store) : IToolHandler
{
    private static Dictionary<string,Field> Provider() => new(){["provider"]=new("string","Verbundenes Konto",Choices:["google","microsoft"])};
    public IReadOnlyList<ToolDefinition> Definitions {get;}=Build();
    private static List<ToolDefinition> Build()
    {
        var list=new List<ToolDefinition>();
        foreach(var action in new[]{"GetEvents","CreateEvent","UpdateEvent","DeleteEvent","GetAvailability"})
        {
            var f=Provider();
            if(action!="DeleteEvent") { f["start"]=new("string","Beginn ISO-8601 mit Zeitzone"); f["end"]=new("string","Ende ISO-8601 mit Zeitzone"); }
            if(action is "CreateEvent" or "UpdateEvent") { f["title"]=new("string","Titel",MaxLength:300); f["description"]=new("string","Beschreibung",false); }
            if(action is "UpdateEvent" or "DeleteEvent") f["eventId"]=new("string","Ereignis-ID");
            list.Add(new("Calendar."+action,"Kalender: "+action,action is "GetEvents" or "GetAvailability"?Risk.Safe:Risk.Confirm,f));
        }
        foreach(var action in new[]{"Search","Read","Draft","Send"})
        {
            var f=Provider();
            if(action=="Search") f["query"]=new("string","Suchanfrage",MaxLength:500);
            if(action=="Read") f["messageId"]=new("string","Nachrichten-ID",MaxLength:500);
            if(action is "Draft" or "Send") { f["to"]=new("string","Empfängeradresse",MaxLength:320); f["subject"]=new("string","Betreff",MaxLength:500); f["body"]=new("string","Nachricht",MaxLength:50000); }
            list.Add(new("Mail."+action,"E-Mail: "+action, action is "Search" or "Read"?Risk.Safe:Risk.Confirm,f));
        }
        list.Add(new("Contacts.Search","Kontakte anhand Name, Telefon oder E-Mail suchen.",Risk.Safe,new(){["query"]=new("string","Suchbegriff",MaxLength:300)}));
        return list;
    }
    private static string E(string text)=>Uri.EscapeDataString(text);
    public async Task<JsonNode?> ExecuteAsync(Actor actor,string name,JsonObject a,CancellationToken ct)
    {
        if(name=="Contacts.Search") return new JsonArray((await store.ListAsync(actor.UserId,"contacts",1000,ct)).Where(d=>d.Data.ToJsonString().Contains(a["query"]!.GetValue<string>(),StringComparison.OrdinalIgnoreCase)).Select(d=>(JsonNode?)d.Data.DeepClone()).ToArray());
        var p=a["provider"]!.GetValue<string>(); var google=p=="google"; var owner=actor.UserId;
        if(name.StartsWith("Calendar."))
        {
            DateTimeOffset start=default,end=default;
            if(name!="Calendar.DeleteEvent") {
                if(!DateTimeOffset.TryParse(a["start"]!.GetValue<string>(),out start)||!DateTimeOffset.TryParse(a["end"]!.GetValue<string>(),out end)||end<=start||end-start>TimeSpan.FromDays(366)) throw new JarvisException("Ungültiger Kalenderzeitraum.");
            }
            var root=google?"calendar/v3/calendars/primary/events":"me/events";
            if(name is "Calendar.GetEvents" or "Calendar.GetAvailability")
            {
                var relative=google?$"{root}?timeMin={E(start.ToString("O"))}&timeMax={E(end.ToString("O"))}&singleEvents=true&orderBy=startTime&maxResults=250":$"me/calendarView?startDateTime={E(start.ToString("O"))}&endDateTime={E(end.ToString("O"))}&$top=250";
                return await oauth.RequestAsync(owner,p,HttpMethod.Get,relative,null,ct);
            }
            if(name=="Calendar.DeleteEvent") return await oauth.RequestAsync(owner,p,HttpMethod.Delete,root+"/"+E(a["eventId"]!.GetValue<string>()),null,ct);
            var payload=google ? new JsonObject {
                ["summary"]=a["title"]!.DeepClone(),["description"]=a["description"]?.DeepClone(),
                ["start"]=new JsonObject{["dateTime"]=start.ToString("O")},["end"]=new JsonObject{["dateTime"]=end.ToString("O")}
            } : new JsonObject {
                ["subject"]=a["title"]!.DeepClone(),["body"]=new JsonObject{["contentType"]="text",["content"]=a["description"]?.GetValue<string>()??""},
                ["start"]=new JsonObject{["dateTime"]=start.UtcDateTime.ToString("s"),["timeZone"]="UTC"},["end"]=new JsonObject{["dateTime"]=end.UtcDateTime.ToString("s"),["timeZone"]="UTC"}
            };
            return await oauth.RequestAsync(owner,p,name=="Calendar.CreateEvent"?HttpMethod.Post:HttpMethod.Patch,root+(name=="Calendar.UpdateEvent"?"/"+E(a["eventId"]!.GetValue<string>()):""),payload,ct);
        }
        if(name=="Mail.Search") return await oauth.RequestAsync(owner,p,HttpMethod.Get,google?"gmail/v1/users/me/messages?maxResults=30&q="+E(a["query"]!.GetValue<string>()):"me/messages?$top=30&$search="+E("\""+a["query"]!.GetValue<string>().Replace("\"","")+"\""),null,ct);
        if(name=="Mail.Read") return await oauth.RequestAsync(owner,p,HttpMethod.Get,(google?"gmail/v1/users/me/messages/":"me/messages/")+E(a["messageId"]!.GetValue<string>()),null,ct);
        var to=a["to"]!.GetValue<string>(); var subject=a["subject"]!.GetValue<string>();
        if(to.Contains('\r')||to.Contains('\n')||subject.Contains('\r')||subject.Contains('\n')||!MailAddress.TryCreate(to,out _)) throw new JarvisException("Ungültiger E-Mail-Empfänger oder Betreff.");
        JsonObject message;
        if(google)
        {
            var mime="To: "+to+"\r\nSubject: =?UTF-8?B?"+Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))+"?=\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(a["body"]!.GetValue<string>()));
            message=new(){["raw"]=Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)).TrimEnd('=').Replace('+','-').Replace('/','_')};
            return await oauth.RequestAsync(owner,p,HttpMethod.Post,name=="Mail.Send"?"gmail/v1/users/me/messages/send":"gmail/v1/users/me/drafts",name=="Mail.Send"?message:new JsonObject{["message"]=message},ct);
        }
        message=new(){["subject"]=subject,["body"]=new JsonObject{["contentType"]="Text",["content"]=a["body"]!.DeepClone()},["toRecipients"]=new JsonArray(new JsonObject{["emailAddress"]=new JsonObject{["address"]=to}})};
        return await oauth.RequestAsync(owner,p,HttpMethod.Post,name=="Mail.Send"?"me/sendMail":"me/messages",name=="Mail.Send"?new JsonObject{["message"]=message,["saveToSentItems"]=true}:message,ct);
    }
}
