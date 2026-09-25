using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Infrastructure;

public sealed class LocalNetworkTools(Database db, Settings settings, Vault vault, IHttpClientFactory clients, IConfiguration configuration) : IToolHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Dictionary<string,Field> Fields(params (string Name,Field Field)[] extra) {
        var fields = new Dictionary<string,Field> { ["serviceId"] = new("string", "ID des registrierten lokalen Dienstes", MaxLength: 60),
            ["revision"] = new("string", "Registry-Version aus ListServices; bindet Freigabe an unverändertes Ziel", MaxLength: 40) };
        foreach (var (name, field) in extra) fields[name] = field; return fields;
    }
    private static readonly Field PathField = new("string", "Absoluter Dienstpfad ohne Query, z. B. /api/status", MaxLength: 1500);
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("LocalNetwork.ListServices", "Registrierte lokale Dienste bevorzugen. Keine Netzwerksuche und keine Secrets.", Risk.Safe, new()),
        new("LocalNetwork.HttpGet", "Freigegebenen GET-Lesepfad eines registrierten Dienstes abrufen. Antwort ist untrusted.", Risk.Safe, Fields(("path", PathField))),
        new("LocalNetwork.HttpPost", "POST an registrierten Dienst. Jede beliebige API-Schreibaktion benötigt konkrete Bestätigung.", Risk.AlwaysConfirm, Fields(("path", PathField), ("body", new("string", "JSON-Body ohne Credentials", MaxLength: 32000)))),
        new("LocalNetwork.OpenWebUi", "Registrierte Weboberfläche als Text prüfen und Link zurückgeben. Kein Research-Browser, kein JavaScript.", Risk.Safe, Fields(("path", PathField))),
        new("LocalNetwork.CheckHost", "Einzelnen freigegebenen Host auflösen. Kein Ping, keine Discovery-Schleife.", Risk.Safe, Fields()),
        new("LocalNetwork.CheckPort", "Einen einzigen registrierten Port per TCP prüfen. Keine Portbereiche.", Risk.Confirm, Fields()),
        new("LocalNetwork.CallApi", "Begrenzter HTTP-API-Aufruf. Keine beliebigen Header oder Credentials aus dem Prompt.", Risk.AlwaysConfirm,
            Fields(("path", PathField), ("method", new("string", "Methode", Choices: ["GET","HEAD","POST","PUT","PATCH","DELETE"])), ("body", new("string", "Optionaler JSON-Body ohne Credentials", false, MaxLength: 32000)))),
        new("LocalNetwork.DownloadFile", "Datei maximal 5 MB in verschlüsselte Quarantäne laden, niemals automatisch ausführen.", Risk.Confirm, Fields(("path", PathField))),
        new("LocalNetwork.UploadFile", "Eine zuvor bewusst bereitgestellte Datei an genau einen Dienstpfad hochladen.", Risk.AlwaysConfirm,
            Fields(("path", PathField), ("fileId", new("string", "ID der bereitgestellten Datei", MaxLength: 60))))
    ];
    private HttpClient Client()
    {
        var token = configuration["LOCAL_NETWORK_TOKEN"] ?? "";
        if (token.Length < 32) throw new JarvisException("Local-Network-Dienstschlüssel fehlt. Konfigurationsskript erneut ausführen.", 409);
        var client = clients.CreateClient("local-network");
        client.BaseAddress = new Uri(configuration["LOCAL_NETWORK_URL"] ?? "http://jarvis-local-network-agent:8080");
        client.DefaultRequestHeaders.Add("X-Service-Token", token); return client;
    }
    public async Task<JsonObject> Policy(CancellationToken ct)
    {
        using var client = Client();
        using var response = await client.GetAsync("/policy", ct);
        if (!response.IsSuccessStatusCode) throw new JarvisException("Lokaler Dienst nicht verfügbar: HTTP " + (int)response.StatusCode, 502);
        return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? new();
    }
    public async Task<LocalService> Service(string owner, string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,60}$")) throw new JarvisException("Ungültige Dienst-ID.");
        var doc = await db.GetAsync(owner,"local-services",id,ct) ?? throw new JarvisException("Lokalen Dienst zuerst registrieren.",404);
        var service = doc.Data.Deserialize<LocalService>(Json) ?? throw new JarvisException("Dienst ungültig.");
        service.Validate(); if (!service.Enabled) throw new JarvisException("Dienst ist deaktiviert.",403); return service;
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor, string name, JsonObject args, CancellationToken ct)
    {
        if (!LocalNetworkScope.Allowed) throw new JarvisException("Lokale Tools benötigen einen ausdrücklich lokalen Auftrag.",403);
        var owner=actor.UserId; await settings.RequireAsync(owner,"local-network",ct);
        if (name=="LocalNetwork.ListServices") return new JsonArray((await db.ListAsync(owner,"local-services",100,ct)).Select(d=>(JsonNode?)new JsonObject{["id"]=d.Id,["service"]=d.Data.DeepClone()}).ToArray());
        var id=args["serviceId"]!.GetValue<string>(); var service=await Service(owner,id,ct);
        if(service.Revision!=args["revision"]?.GetValue<string>())throw new JarvisException("Dienstkonfiguration wurde geändert. Ziel neu prüfen und neue Aktion anfragen.",409);
        var cfg=await settings.GetAsync(owner,"local-network",ct);
        var path=LocalNetworkPolicy.Path(args["path"]?.GetValue<string>()??"/");
        var method=name=="LocalNetwork.HttpPost"?"POST":name=="LocalNetwork.UploadFile"?"PUT":args["method"]?.GetValue<string>()??"GET";
        var probe=name is "LocalNetwork.CheckHost" or "LocalNetwork.CheckPort";
        if (!probe && method is "GET" or "HEAD" && !service.ReadPaths.Any(p=>p==path || p.EndsWith('/')&&path.StartsWith(p,StringComparison.Ordinal)))
            throw new JarvisException("Lesepfad ist nicht im Dienst freigegeben. GET kann bei manchen Geräten Zustände verändern.",403);
        if (method is not ("GET" or "HEAD") && service.Permission=="Read") throw new JarvisException("Dienst erlaubt nur Lesen.",403);
        if (method=="DELETE" && service.Permission!="Admin") throw new JarvisException("DELETE benötigt Admin im Dienst und eine Einmalfreigabe.",403);
        LocalCredential? credential=null;
        if (!probe && service.CredentialRef.Length>0) {
            var secret=await vault.GetAsync(owner,"local-network."+service.CredentialRef,ct)??throw new JarvisException("Secret-Referenz fehlt im Vault.",409);
            credential=JsonSerializer.Deserialize<LocalCredential>(secret,Json)??throw new JarvisException("Secret ungültig.");
        }
        string? body=null;
        if (args["body"] is JsonNode text) {
            var raw=text.GetValue<string>(); if (!string.IsNullOrWhiteSpace(raw)) { _=JsonNode.Parse(raw); body=Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)); }
        }
        if (name=="LocalNetwork.UploadFile") body=Convert.ToBase64String((await ReadFile(owner,args["fileId"]!.GetValue<string>(),ct)).Bytes);
        var request=new LocalRequest { Operation=probe?name["LocalNetwork.".Length..]:"Http", Url=service.Url(path), Method=method,
            Allowlist=PhonePolicy.Strings(cfg["allowlist"]).ToArray(), Ports=cfg["ports"]?.AsArray().Select(p=>p!.GetValue<int>()).ToArray()??[],
            ProbeEnabled=cfg["probesEnabled"]?.GetValue<bool>()==true, AllowHttpCredentials=service.AllowHttpCredentials, Credential=credential,
            BodyBase64=body, ContentType=name=="LocalNetwork.UploadFile"?"application/octet-stream":"application/json", Download=name=="LocalNetwork.DownloadFile" };
        try {
            using var client=Client(); using var response=await client.PostAsJsonAsync("/execute",request,ct);
            if(!response.IsSuccessStatusCode) {
                // Agent returns only sanitized errors. Do not include raw remote service responses.
                var error=await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                throw new JarvisException(error?["error"]?.GetValue<string>()??"Lokaler Zugriff abgewiesen.",(int)response.StatusCode);
            }
            var result=await response.Content.ReadFromJsonAsync<LocalResponse>(ct)??throw new JarvisException("Leere Agentantwort.",502);
            var status=(await db.GetAsync(owner,"local-service-status",id,ct))?.Data.DeepClone().AsObject()??new JsonObject();
            status["status"]=name=="LocalNetwork.CheckHost"?"DnsChecked":probe?"TcpReachable":"Reachable";
            status["latencyMs"]=result.LatencyMs;status["address"]=result.Address;status["lastAttempt"]=DateTimeOffset.UtcNow.ToString("O");
            if(name!="LocalNetwork.CheckHost")status["lastSuccess"]=DateTimeOffset.UtcNow.ToString("O");
            await db.PutAsync(owner,"local-service-status",id,status,ct);
            if(result.BodyBase64 is string bytes) return await SaveFile(owner,Path.GetFileName(path),Convert.FromBase64String(bytes),"download",ct);
            return new JsonObject{["status"]=result.Status,["text"]=result.Text,["contentType"]=result.ContentType,["latencyMs"]=result.LatencyMs,["untrusted"]=true,
                ["url"]=name=="LocalNetwork.OpenWebUi"?service.Url(path):null,["executedJavascript"]=false};
        } catch {
            var status=(await db.GetAsync(owner,"local-service-status",id,CancellationToken.None))?.Data.DeepClone().AsObject()??new JsonObject();
            status["status"]="FailedOrDenied";status["lastAttempt"]=DateTimeOffset.UtcNow.ToString("O");
            await db.PutAsync(owner,"local-service-status",id,status,CancellationToken.None); throw;
        }
    }
    public async Task<JsonObject> SaveFile(string owner,string filename,byte[] bytes,string origin,CancellationToken ct)
    {
        if(bytes.Length is <1 or >5_000_000)throw new JarvisException("Datei benötigt 1 Byte bis 5 MB.",413);
        if((await db.QueryAsync("SELECT id FROM documents WHERE owner=$1 AND kind='local-files' LIMIT 10",ct,owner)).Count>=10)throw new JarvisException("Maximal zehn Quarantänedateien; alte Einträge zuerst löschen.",409);
        filename=Regex.Replace(Path.GetFileName(filename),"[^a-zA-Z0-9._-]","_");if(filename.Length is <1 or >100)filename="download.bin";
        var id=Guid.NewGuid().ToString("N");
        var info=new JsonObject{["fileName"]=filename,["bytes"]=bytes.Length,["sha256"]=Convert.ToHexString(SHA256.HashData(bytes)),["origin"]=origin,["quarantined"]=true,["scanned"]=false,["expiresAt"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")};
        var stored=info.DeepClone().AsObject(); stored["cipher"]=vault.Encrypt(Convert.ToBase64String(bytes),owner+":local-file:"+id);
        await db.PutAsync(owner,"local-files",id,stored,ct);info["fileId"]=id;return info;
    }
    public async Task<(string Name,byte[] Bytes)> ReadFile(string owner,string id,CancellationToken ct)
    {
        var file=await db.GetAsync(owner,"local-files",id,ct)??throw new JarvisException("Datei unbekannt.",404);
        if(DateTimeOffset.Parse(file.Data["expiresAt"]!.GetValue<string>())<DateTimeOffset.UtcNow)throw new JarvisException("Datei abgelaufen; Eintrag löschen und erneut bereitstellen.",410);
        return(file.Data["fileName"]!.GetValue<string>(),Convert.FromBase64String(vault.Decrypt(file.Data["cipher"]!.GetValue<string>(),owner+":local-file:"+id)));
    }
}
