using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;

namespace Jarvis.Infrastructure;

public sealed class NextcloudIndex(NextcloudTools files, ConnectorHttp http, Settings settings, AiClient ai, IDocumentStore store, Vault vault) : IToolHandler
{
    public IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new("Nextcloud.Index", "Geänderte Nextcloud-Dokumente anhand ETags indexieren. Überträgt Auszüge an den Embedding-Provider; Originale bleiben unverändert.", Risk.Confirm,
            new() { ["maxFiles"] = new("integer", "Maximal 500 Dateien", false) }, 600),
        new("Nextcloud.SemanticSearch", "Semantisch im aktivierten Nextcloud-Index suchen; ETags der Treffer erneut prüfen.", Risk.Safe,
            new() { ["query"] = new("string", "Suchfrage", MaxLength: 500) }, 90)
    ];
    private async Task<(string Fingerprint,string Model)> Configuration(string owner,CancellationToken ct)
    {
        await settings.RequireAsync(owner,"nextcloud",ct);
        var cfg=await settings.GetAsync(owner,"nextcloud",ct);
        if(cfg["indexEnabled"]?.GetValue<bool>()!=true)throw new JarvisException("Nextcloud-Indizierung ist nicht freigegeben.",403);
        var model=(await settings.GetAsync(owner,"ai",ct))["embeddingModel"]?.GetValue<string>()??throw new JarvisException("Embedding-Modell fehlt.");
        return(Crypto.Hash(cfg["url"]?.GetValue<string>()+":"+cfg["username"]?.GetValue<string>()+":"+cfg["root"]?.GetValue<string>()),model);
    }
    public async Task<JsonNode?> ExecuteAsync(Actor actor,string name,JsonObject args,CancellationToken ct)
    {
        var owner=actor.UserId;var(fingerprint,model)=await Configuration(owner,ct);
        if(name=="Nextcloud.SemanticSearch") {
            var vector=await ai.EmbedAsync(owner,args["query"]!.GetValue<string>(),model,ct);
            var ranked=new List<(double Score,JsonObject Data)>();
            foreach(var stored in await store.ListAsync(owner,"nextcloud-index",1000,ct)){
                if(stored.Data["fingerprint"]?.GetValue<string>()!=fingerprint||stored.Data["model"]?.GetValue<string>()!=model)continue;
                var data=JsonNode.Parse(vault.Decrypt(stored.Data["cipher"]!.GetValue<string>(),owner+":nextcloud-index:"+stored.Id))!.AsObject();
                var other=data["vector"]!.Deserialize<double[]>()!;if(other.Length!=vector.Length)continue;
                var denominator=Math.Sqrt(vector.Sum(x=>x*x)*other.Sum(x=>x*x));var score=denominator==0?0:vector.Zip(other).Sum(x=>x.First*x.Second)/denominator;
                if(score>0.25)ranked.Add((score,data));
            }
            var results=new JsonArray();
            foreach(var match in ranked.OrderByDescending(x=>x.Score).Take(8)){
                var path=match.Data["path"]!.GetValue<string>();
                try {
                    var current=await http.RequestAsync(owner,"nextcloud",HttpMethod.Head,await files.Relative(owner,path,ct),null,null,ct);
                    if(current.Etag!=match.Data["etag"]?.GetValue<string>())continue;
                    results.Add(new JsonObject{["path"]=path,["text"]=match.Data["text"]!.DeepClone(),["similarity"]=match.Score,["untrusted"]=true});
                }catch(JarvisException){/* Deleted, inaccessible or changed entries never become authoritative context. */}
            }
            return results;
        }
        var maximum=Math.Clamp(args["maxFiles"]?.GetValue<int>()??100,1,500);var visited=0;var changed=0;var unchanged=0;var failed=0;
        var folders=new Queue<string>();folders.Enqueue("/");var seen=new HashSet<string>();
        while(folders.Count>0 && visited<maximum && seen.Count<1000){
            var folder=folders.Dequeue();if(!seen.Add(folder))continue;
            foreach(var item in await files.List(owner,folder,ct)){
                var path=item!["path"]!.GetValue<string>();if(path.Trim('/')==folder.Trim('/'))continue;
                if(item["folder"]?.GetValue<bool>()==true){folders.Enqueue(path);continue;}
                if(visited++>=maximum)break;
                if(!new[]{".pdf",".docx",".xlsx",".pptx",".txt",".md",".csv",".tsv"}.Contains(Path.GetExtension(path).ToLowerInvariant()))continue;
                var id=Crypto.Hash(fingerprint+":"+path);var etag=item["etag"]?.GetValue<string>();
                var previous=await store.GetAsync(owner,"nextcloud-index",id,ct);
                if(etag is not null && previous?.Data["etag"]?.GetValue<string>()==etag&&previous.Data["model"]?.GetValue<string>()==model){unchanged++;continue;}
                try {
                    var extracted=await files.ExecuteAsync(actor,"Nextcloud.Read",new(){["path"]=path},ct);
                    var text=extracted?.ToJsonString()??"";text=text[..Math.Min(text.Length,16000)];
                    var vector=await ai.EmbedAsync(owner,text,model,ct);
                    var data=new JsonObject{["path"]=path,["etag"]=etag,["text"]=text,["vector"]=JsonSerializer.SerializeToNode(vector)};
                    await store.PutAsync(owner,"nextcloud-index",id,new(){["fingerprint"]=fingerprint,["etag"]=etag,["model"]=model,["cipher"]=vault.Encrypt(data.ToJsonString(),owner+":nextcloud-index:"+id)},ct);changed++;
                }catch(JarvisException e)when(e.Status is not (429 or 403)){failed++;}
            }
        }
        return new JsonObject{["indexed"]=changed,["unchanged"]=unchanged,["failed"]=failed,["visited"]=visited,["boundedScan"]=true};
    }
}
