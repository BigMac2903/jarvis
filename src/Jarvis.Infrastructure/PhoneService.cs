using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public sealed class PhoneService(Settings settings,Vault vault,IDocumentStore store,IHttpClientFactory clients,IConfiguration config):IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions {get;}=[
        new("Phone.Call","Ausgehenden Anruf starten. JARVIS stellt sich als digitaler Assistent vor.",Risk.Confirm,new(){["number"]=new("string","E.164 Telefonnummer",MaxLength:20),["objective"]=new("string","Abgesprochener Gesprächsauftrag",MaxLength:5000)},60),
        new("Phone.Hangup","Aktiven Anruf beenden.",Risk.Confirm,new(){["callId"]=new("string","JARVIS Anruf-ID",MaxLength:100)}),
        new("Phone.SendDtmf","DTMF-Tasten an laufenden Anruf senden.",Risk.Confirm,new(){["callId"]=new("string","Anruf-ID",MaxLength:100),["digits"]=new("string","Ziffern, Stern oder Raute",MaxLength:30)})
    ];
    public async Task<JsonNode?> ExecuteAsync(Actor actor,string name,JsonObject a,CancellationToken ct)
    {
        var cfg=await settings.GetAsync(actor.UserId,"twilio",ct);
        if(name=="Phone.Call")
        {
            await settings.RequireAsync(actor.UserId,"ai",ct);
            var number=a["number"]!.GetValue<string>();if(!Regex.IsMatch(number,@"^\+[1-9]\d{6,14}$"))throw new JarvisException("E.164 Telefonnummer erforderlich.");
            var publicUrl=config["PUBLIC_URL"]!.TrimEnd('/');
            if(!publicUrl.StartsWith("https://")||new Uri(publicUrl).IsLoopback)throw new JarvisException("Telefonie benötigt eine öffentlich erreichbare HTTPS-Domain.",409);
            var id=a["callId"]?.GetValue<string>()??Guid.NewGuid().ToString("N");
            var call=new JsonObject{["provider"]="twilio",["direction"]="outbound",["started_at"]=DateTimeOffset.UtcNow.ToString("O"),["number"]=number,["objective"]=a["objective"]!.DeepClone(),["status"]="initiating",["recording"]=false,["transcript"]=new JsonArray()};
            await store.PutAsync(actor.UserId,"calls",id,call,ct);
            var path="/api/v1/phone/webhook/"+actor.UserId+"/"+id;
            var result=await Request(actor.UserId,HttpMethod.Post,"Calls.json",new(){
                ["To"]=number,["From"]=cfg["callerId"]?.GetValue<string>()??cfg["number"]?.GetValue<string>()??throw new JarvisException("Caller-ID fehlt."),
                ["Url"]=publicUrl+path,["StatusCallback"]=publicUrl+path+"/status",["StatusCallbackEvent"]="completed",
                ["Record"]="false",["Timeout"]="30",["TimeLimit"]=(a["max_duration"]?.GetValue<int>()??600).ToString()
            },ct);
            call["sid"]=result!["sid"]!.DeepClone();call["status"]=result["status"]!.DeepClone();
            await store.PutAsync(actor.UserId,"calls",id,call,ct);return new JsonObject{["callId"]=id,["status"]=call["status"]!.DeepClone()};
        }
        var callId=a["callId"]!.GetValue<string>();var existing=await store.GetAsync(actor.UserId,"calls",callId,ct)??throw new JarvisException("Anruf nicht gefunden.",404);
        var sid=existing.Data["sid"]?.GetValue<string>()??throw new JarvisException("Anruf noch nicht verbunden.");
        if(name=="Phone.Hangup")return await Request(actor.UserId,HttpMethod.Post,"Calls/"+Uri.EscapeDataString(sid)+".json",new(){["Status"]="completed"},ct);
        var digits=a["digits"]!.GetValue<string>();if(!Regex.IsMatch(digits,@"^[0-9*#w]+$"))throw new JarvisException("Ungültige DTMF-Zeichen.");
        var xml=new XElement("Response",new XElement("Play",new XAttribute("digits",digits)),new XElement("Redirect",config["PUBLIC_URL"]!.TrimEnd('/')+"/api/v1/phone/webhook/"+actor.UserId+"/"+callId));
        return await Request(actor.UserId,HttpMethod.Post,"Calls/"+Uri.EscapeDataString(sid)+".json",new(){["Twiml"]=xml.ToString()},ct);
    }
    private async Task<JsonNode?> Request(string owner,HttpMethod method,string path,Dictionary<string,string>? data,CancellationToken ct)
    {
        await settings.RequireAsync(owner,"twilio",ct);
        var cfg=await settings.GetAsync(owner,"twilio",ct);
        var sid=cfg["accountSid"]?.GetValue<string>()??throw new JarvisException("Twilio SID fehlt.");
        if(!Regex.IsMatch(sid,"^AC[a-fA-F0-9]{32}$"))throw new JarvisException("Twilio SID ungültig.");
        var token=await vault.GetAsync(owner,"twilio.authToken",ct)??throw new JarvisException("Twilio Token fehlt.");
        using var client=clients.CreateClient("provider");
        using var req=new HttpRequestMessage(method,"https://api.twilio.com/2010-04-01/Accounts/"+sid+"/"+path);
        req.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.ASCII.GetBytes(sid+":"+token)));
        if(data is not null)req.Content=new FormUrlEncodedContent(data);
        using var response=await client.SendAsync(req,ct);
        if(!response.IsSuccessStatusCode)throw new JarvisException($"Twilio: HTTP {(int)response.StatusCode}",502);
        return await response.Content.ReadFromJsonAsync<JsonNode>(ct);
    }
    public async Task TestAsync(string owner,CancellationToken ct)=>_ = await Request(owner,HttpMethod.Get,"IncomingPhoneNumbers.json?PageSize=1",null,ct);
    public async Task ValidateSignatureAsync(string owner,string path,string signature,IEnumerable<KeyValuePair<string,string>> fields,CancellationToken ct)
    {
        var token=await vault.GetAsync(owner,"twilio.authToken",ct)??throw new JarvisException("Twilio nicht konfiguriert.",403);
        var data=config["PUBLIC_URL"]!.TrimEnd('/')+path;
        foreach(var field in fields.OrderBy(x=>x.Key,StringComparer.Ordinal))data+=field.Key+field.Value;
        var expected=Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token),Encoding.UTF8.GetBytes(data)));
        if(!Crypto.EqualsSecret(signature,expected))throw new JarvisException("Webhook-Signatur ungültig.",403);
    }
}
