using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Jarvis.Sip;

public sealed class LiveCall : IDisposable
{
    public SipDial Request { get; }
    public SIPUserAgent UserAgent { get; }
    public RTPSession Media { get; }
    private readonly IConfiguration config;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<byte[]> microphone = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<byte[]> speaker = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1500) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<byte[]> signals = Channel.CreateBounded<byte[]>(20);
    private string codec = "pcmu";
    private int payloadType;
    private int stopped;
    public CancellationToken Token => lifetime.Token;

    public LiveCall(SipDial request, SIPUserAgent ua, RTPSession media, IConfiguration configuration, CancellationToken stopping)
    {
        Request = request; UserAgent = ua; Media = media; config = configuration;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lifetime.CancelAfter(TimeSpan.FromSeconds(request.MaxDuration + 30));
        ua.OnCallHungup += _ => Cancel();
        media.OnTimeout += _ => Cancel();
        media.OnAudioFormatsNegotiated += formats => {
            var selected = formats.First(f => f.Codec is AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
            codec = selected.Codec == AudioCodecsEnum.PCMA ? "pcma" : "pcmu";
            payloadType = selected.FormatID;
        };
        media.OnRtpPacketReceived += (_, type, packet) => {
            if (type == SDPMediaTypesEnum.audio && packet.Header.PayloadType == payloadType && packet.Payload.Length <= 1600)
                microphone.Writer.TryWrite(packet.Payload.ToArray());
        };
        // DTMF is delivered as a transient signal; digits are not written to logs or memory.
        ua.OnDtmfTone += (tone, _) => signals.Writer.TryWrite(Encoding.UTF8.GetBytes("{\"type\":\"dtmf\",\"tone\":" + tone + "}"));
    }

    public async Task BridgeAsync()
    {
        lifetime.CancelAfter(TimeSpan.FromSeconds(Request.MaxDuration));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Service-Token", config["SIP_SERVICE_TOKEN"]);
        var uri = new UriBuilder(config["CORE_URL"] ?? "http://jarvis-api:8080") {
            Scheme = (config["CORE_URL"] ?? "http:").StartsWith("https:") ? "wss" : "ws",
            Path = "/internal/sip/media/" + Request.CallId,
            Query = "owner=" + Uri.EscapeDataString(Request.Owner) + "&codec=" + codec
        };
        await socket.ConnectAsync(uri.Uri, Token);
        var jobs = new[] { Upload(socket), Receive(socket), Playback() };
        try { await Task.WhenAny(jobs); }
        finally { lifetime.Cancel(); try { await Task.WhenAll(jobs); } catch (OperationCanceledException) { } }
    }

    private async Task Upload(ClientWebSocket socket)
    {
        while (!Token.IsCancellationRequested)
        {
            while (signals.Reader.TryRead(out var signal)) await socket.SendAsync(signal, WebSocketMessageType.Text, true, Token);
            var audio = await microphone.Reader.ReadAsync(Token);
            await socket.SendAsync(audio, WebSocketMessageType.Binary, true, Token);
        }
    }
    private async Task Receive(ClientWebSocket socket)
    {
        var buffer = new byte[65536];
        var pending = new List<byte>();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                // Only the core can issue clear; no audio backlog survives a barge-in.
                while (speaker.Reader.TryRead(out _)) { }
                pending.Clear(); continue;
            }
            pending.AddRange(buffer.AsSpan(0, result.Count).ToArray());
            var offset = 0;
            while (pending.Count - offset >= 160) { speaker.Writer.TryWrite(pending.GetRange(offset, 160).ToArray()); offset += 160; }
            if (offset > 0) pending.RemoveRange(0, offset);
        }
    }
    private async Task Playback()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var silence = Enumerable.Repeat(codec == "pcma" ? (byte)0xd5 : (byte)0xff, 160).ToArray();
        while (await timer.WaitForNextTickAsync(Token))
            if (UserAgent.IsCallActive && !UserAgent.IsOnLocalHold) Media.SendAudio(160, speaker.Reader.TryRead(out var frame) ? frame : silence);
    }
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lifetime.Cancel();
        if (UserAgent.IsCallActive) UserAgent.Hangup(); else UserAgent.Cancel();
        Media.Close("Call ended");
    }
    private void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { Stop(); UserAgent.Dispose(); Media.Dispose(); lifetime.Dispose(); }
}
