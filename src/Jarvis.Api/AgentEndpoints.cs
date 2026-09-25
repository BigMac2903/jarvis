using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Application;
using Jarvis.Domain;
using Jarvis.Infrastructure;
namespace Jarvis.Api;
public static class AgentEndpoints
{
    public record ChatRequest(string Message,string? Conversation=null,string? Image=null);
    public record ToolRequest(string Name,JsonObject Args);
    public record ResearchRequest(string Question,string Mode,bool Fresh);
    public static void MapAgent(this WebApplication app)
    {
        app.MapPost("/api/v1/chat",async(ChatRequest request,HttpContext ctx,Orchestrator agent,CancellationToken ct)=>
            await agent.ChatAsync(ctx.Owner(),request.Conversation??Guid.NewGuid().ToString("N"),request.Message,request.Image,ct));
        app.MapPost("/api/v1/tools/execute",async(ToolRequest request,HttpContext ctx,ToolDispatcher tools,CancellationToken ct)=>
            await tools.ExecuteAsync(new(ctx.Owner()),request.Name,request.Args,null,ct));
        app.MapPost("/api/v1/research",async(ResearchRequest request,HttpContext ctx,Database db,CancellationToken ct)=>{
            if(request.Question.Length is <3 or >10000||request.Mode is not ("quick" or "research" or "deep"))throw new JarvisException("Ungültige Recherche.");
            var id=Guid.NewGuid().ToString("N");
            await db.PutAsync(ctx.Owner(),"jobs",id,new(){["question"]=request.Question,["mode"]=request.Mode,["fresh"]=request.Fresh,["status"]="pending",["createdAt"]=DateTimeOffset.UtcNow.ToString("O")},ct);
            return Results.Accepted("/api/v1/research/"+id,new{id});
        });
        app.MapGet("/api/v1/research",async(HttpContext ctx,Database db,CancellationToken ct)=>await db.ListAsync(ctx.Owner(),"jobs",100,ct));
        app.MapGet("/api/v1/research/{id}",async(string id,HttpContext ctx,Database db,CancellationToken ct)=>
            await db.GetAsync(ctx.Owner(),"jobs",id,ct)??throw new JarvisException("Recherche nicht gefunden.",404));
        app.MapPost("/api/v1/research/{id}/cancel",async(string id,HttpContext ctx,Database db,JobWorker worker,CancellationToken ct)=>{
            var doc=await db.GetAsync(ctx.Owner(),"jobs",id,ct)??throw new JarvisException("Recherche nicht gefunden.",404);
            worker.Cancel(id);if(doc.Data["status"]?.GetValue<string>()=="pending"){doc.Data["status"]="cancelled";await db.PutAsync(ctx.Owner(),"jobs",id,doc.Data,ct);}return Results.Ok();
        });
        app.MapPost("/api/v1/voice/session",async(HttpContext ctx,AiClient ai,Settings settings,CancellationToken ct)=>{
            await settings.RequireAsync(ctx.Owner(),"voice",ct);
            using var reader=new StreamReader(ctx.Request.Body);var sdp=await reader.ReadToEndAsync(ct);
            if(sdp.Length>100000||!sdp.StartsWith("v="))throw new JarvisException("Ungültiges SDP.");
            var(client,cfg)=await ai.ClientAsync(ctx.Owner(),ct);using var dispose=client;
            if(client.BaseAddress!.Host!="api.openai.com")throw new JarvisException("Realtime WebRTC erfordert einen OpenAI-Provider.",409);
            var model=cfg["realtimeModel"]?.GetValue<string>()??throw new JarvisException("Realtime-Modell fehlt.");
            var session=new JsonObject{["type"]="realtime",["model"]=model,
                ["instructions"]="Du bist die Sprachschnittstelle von JARVIS. Delegiere jede Sachfrage und jede Aktion an ask_jarvis. Sprich dessen Ergebnis natürlich aus. Behaupte nie ungeprüfte Aktionen. Freigaben erfolgen im Dashboard.",
                ["audio"]=new JsonObject{["input"]=new JsonObject{["turn_detection"]=new JsonObject{["type"]="server_vad",["interrupt_response"]=true}},["output"]=new JsonObject{["voice"]="marin"}},
                ["tools"]=new JsonArray(new JsonObject{["type"]="function",["name"]="ask_jarvis",["description"]="Gemeinsamen JARVIS-Agenten nach Informationen fragen oder Aktionen anfragen.",
                    ["parameters"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["message"]=new JsonObject{["type"]="string"}},["required"]=new JsonArray("message"),["additionalProperties"]=false}})};
            using var form=new MultipartFormDataContent();form.Add(new StringContent(sdp),"sdp");form.Add(new StringContent(session.ToJsonString()),"session");
            using var response=await client.PostAsync("realtime/calls",form,ct);
            if(!response.IsSuccessStatusCode)throw new JarvisException($"Realtime-Verbindung: HTTP {(int)response.StatusCode}",502);
            return Results.Text(await response.Content.ReadAsStringAsync(ct),"application/sdp");
        });
    }
}
