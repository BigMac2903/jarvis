using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;
public sealed partial class AiClient
{
    public async IAsyncEnumerable<string> StreamTextAsync(string owner,string instructions,string prompt,[EnumeratorCancellation] CancellationToken ct)
    {
        var(client,cfg)=await ClientAsync(owner,ct);using var dispose=client;
        var compatible=cfg["protocol"]?.GetValue<string>()=="chat";
        var model=cfg["model"]?.GetValue<string>()??throw new JarvisException("KI-Modell fehlt.");
        var payload=compatible?new JsonObject{["model"]=model,["stream"]=true,["messages"]=new JsonArray(new JsonObject{["role"]="system",["content"]=instructions},new JsonObject{["role"]="user",["content"]=prompt})}
            :new JsonObject{["model"]=model,["stream"]=true,["store"]=false,["instructions"]=instructions,["input"]=prompt,["max_output_tokens"]=1200};
        using var request=new HttpRequestMessage(HttpMethod.Post,compatible?"chat/completions":"responses"){Content=JsonContent.Create(payload)};
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new JarvisException($"KI-Stream: HTTP {(int)response.StatusCode}",502);
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while(await reader.ReadLineAsync(ct) is string line)
        {
            if(!line.StartsWith("data: "))continue;
            var data=line[6..];if(data=="[DONE]")yield break;
            var frame=JsonNode.Parse(data)!;
            var delta=compatible?frame["choices"]?[0]?["delta"]?["content"]?.GetValue<string>():
                frame["type"]?.GetValue<string>()=="response.output_text.delta"?frame["delta"]?.GetValue<string>():null;
            if(!string.IsNullOrEmpty(delta))yield return delta;
            if(frame["type"]?.GetValue<string>() is "error" or "response.failed")throw new JarvisException("KI-Stream abgebrochen.",502);
        }
    }
}
