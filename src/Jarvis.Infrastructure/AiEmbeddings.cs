using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
namespace Jarvis.Infrastructure;

public sealed partial class AiClient
{
    public async Task<double[]> EmbedAsync(string owner,string input,string model,CancellationToken ct)
    {
        var choice=await router.NamedAsync(owner,model,0,ct);
        var reservation=await router.ReserveAsync(owner,choice,System.Text.Encoding.UTF8.GetByteCount(input),"embedding",ct);
        var timer=System.Diagnostics.Stopwatch.StartNew();JsonObject? body=null;
        try {
            var(client,_)=await ClientAsync(owner,ct);using var dispose=client;
            using var response=await client.PostAsJsonAsync("embeddings",new{model,input},ct);
            if(!response.IsSuccessStatusCode)throw new JarvisException($"Embedding-Provider: HTTP {(int)response.StatusCode}",502);
            body=await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            var vector=JsonSerializer.Deserialize<double[]>(body!["data"]![0]!["embedding"]!.ToJsonString())!;
            if(vector.Length is <1 or >10000||vector.Any(x=>!double.IsFinite(x)))throw new JarvisException("Ungültiges Embedding.",502);
            return vector;
        } finally {await router.CompleteAsync(reservation,choice,body?["usage"],timer.ElapsedMilliseconds,body is not null,CancellationToken.None);}
    }
}
