using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using Microsoft.Extensions.Configuration;
namespace Jarvis.Infrastructure;
public sealed class SemanticMemory(IDocumentStore store,Settings settings,AiClient ai,IConfiguration config)
{
    public async Task<JsonObject> IndexAsync(string owner,int maxFiles,CancellationToken ct)
    {
        var cfg=await settings.GetAsync(owner,"ai",ct);
        var model=cfg["embeddingModel"]?.GetValue<string>();
        if(string.IsNullOrWhiteSpace(model))throw new JarvisException("Embedding-Modell zuerst unter AI Models konfigurieren.",409);
        var indexed=0;
        // Legacy memories have no sensitivity classification. Never upload them implicitly.
        var vault=await settings.GetAsync(owner,"obsidian",ct);
        var root=config["OBSIDIAN_PATH"]??"/data/obsidian";
        if(vault["enabled"]?.GetValue<bool>()==true&&Directory.Exists(root))
            foreach(var path in Directory.EnumerateFiles(root,"*.md",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Take(maxFiles)){
                if(new FileInfo(path).Length>1000000)continue;
                var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                _=MemoryTools.SafePath(root,relative);
                await Index(owner,"obsidian",relative,await File.ReadAllTextAsync(path,ct),model,ct);indexed++;
            }
        return new(){["indexed"]=indexed,["model"]=model};
    }
    private async Task Index(string owner,string kind,string id,string text,string model,CancellationToken ct)
    {
        var clipped=text[..Math.Min(text.Length,16000)];var hash=Crypto.Hash(text);var key=Crypto.Hash(kind+":"+id);
        var previous=await store.GetAsync(owner,"vectors",key,ct);
        if(previous?.Data["hash"]?.GetValue<string>()==hash&&previous.Data["model"]?.GetValue<string>()==model)return;
        var vector=await Embed(owner,clipped,model,ct);
        await store.PutAsync(owner,"vectors",key,new(){["sourceKind"]=kind,["sourceId"]=id,["hash"]=hash,["model"]=model,["text"]=clipped,["vector"]=JsonSerializer.SerializeToNode(vector)},ct);
    }
    public async Task<JsonArray> SearchAsync(string owner,string query,CancellationToken ct)
    {
        var cfg=await settings.GetAsync(owner,"ai",ct);var model=cfg["embeddingModel"]?.GetValue<string>();
        if(cfg["enabled"]?.GetValue<bool>()!=true||string.IsNullOrWhiteSpace(model))return [];
        var docs=(await store.ListAsync(owner,"vectors",1000,ct)).Where(d=>d.Data["model"]?.GetValue<string>()==model).ToArray();
        if(docs.Length==0)return [];
        var vector=await Embed(owner,query,model,ct);var matches=new List<(double Score,JsonObject Data)>();
        var obsidian=await settings.GetAsync(owner,"obsidian",ct);
        foreach(var doc in docs){
            var data=doc.Data;var kind=data["sourceKind"]!.GetValue<string>();var id=data["sourceId"]!.GetValue<string>();
            if(kind=="obsidian"){
                if(obsidian["enabled"]?.GetValue<bool>()!=true)continue;
                var path=MemoryTools.SafePath(config["OBSIDIAN_PATH"]??"/data/obsidian",id);
                if(!File.Exists(path))continue;
                if(new FileInfo(path).Length>1000000)continue;
                if(Crypto.Hash(await File.ReadAllTextAsync(path,ct))!=data["hash"]!.GetValue<string>())continue;
            }else continue;
            var other=JsonSerializer.Deserialize<double[]>(data["vector"]!.ToJsonString())!;
            if(other.Length!=vector.Length)continue;
            var dot=vector.Zip(other).Sum(p=>p.First*p.Second);var denominator=Math.Sqrt(vector.Sum(x=>x*x)*other.Sum(x=>x*x));
            var score=denominator==0?0:dot/denominator;
            if(score>0.3)matches.Add((score,new(){["sourceKind"]=kind,["sourceId"]=id,["text"]=data["text"]!.DeepClone(),["similarity"]=score,["untrusted"]=true}));
        }
        return new JsonArray(matches.OrderByDescending(m=>m.Score).Take(8).Select(m=>(JsonNode?)m.Data).ToArray());
    }
    private async Task<double[]> Embed(string owner,string input,string model,CancellationToken ct)
    {
        return await ai.EmbedAsync(owner,input,model,ct);
    }
}
