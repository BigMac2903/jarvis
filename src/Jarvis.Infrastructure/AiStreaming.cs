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
        var choice=(await router.ChooseAsync(owner,prompt,(prompt.Length+instructions.Length)/3,0,false,cfg,ct))[0] with {OutputTokens=1200};
        var model=choice.Model;
        var payload=compatible?new JsonObject{["model"]=model,["stream"]=true,["messages"]=new JsonArray(new JsonObject{["role"]="system",["content"]=instructions},new JsonObject{["role"]="user",["content"]=prompt})}
            :new JsonObject{["model"]=model,["stream"]=true,["store"]=false,["instructions"]=instructions,["input"]=prompt,["max_output_tokens"]=1200};
        payload[compatible?(choice.ReasoningEffort is null?"max_tokens":"max_completion_tokens"):"max_output_tokens"]=1200;
        if(choice.ReasoningEffort is not null) {
            if(compatible)payload["reasoning_effort"]=choice.ReasoningEffort;
            else payload["reasoning"]=new JsonObject{["effort"]=choice.ReasoningEffort};
        }
        if(compatible)payload["stream_options"]=new JsonObject{["include_usage"]=true};
        using var request=new HttpRequestMessage(HttpMethod.Post,compatible?"chat/completions":"responses"){Content=JsonContent.Create(payload)};
        var reservation=await router.ReserveAsync(owner,choice,System.Text.Encoding.UTF8.GetByteCount(payload.ToJsonString()),"stream",ct);
        var timer=System.Diagnostics.Stopwatch.StartNew();JsonNode? usage=null;var complete=false;
        try {
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new JarvisException($"KI-Stream: HTTP {(int)response.StatusCode}",502);
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while(await reader.ReadLineAsync(ct) is string line)
        {
            if(!line.StartsWith("data: "))continue;
            var data=line[6..];if(data=="[DONE]"){complete=true;yield break;}
            var frame=JsonNode.Parse(data)!;
            usage=frame["usage"]??frame["response"]?["usage"]??usage;
            if(frame["type"]?.GetValue<string>()=="response.completed")complete=true;
            var delta=compatible?(frame["choices"] is JsonArray {Count:>0} choices?choices[0]?["delta"]?["content"]?.GetValue<string>():null):
                frame["type"]?.GetValue<string>()=="response.output_text.delta"?frame["delta"]?.GetValue<string>():null;
            if(!string.IsNullOrEmpty(delta))yield return delta;
            if(frame["type"]?.GetValue<string>() is "error" or "response.failed")throw new JarvisException("KI-Stream abgebrochen.",502);
        }
        } finally {await router.CompleteAsync(reservation,choice,usage,timer.ElapsedMilliseconds,complete,CancellationToken.None);}
    }
}
