using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Domain;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Jarvis.Sip;

public record SipDial(string Owner, string CallId, string AccountId, string Number, int MaxDuration);
public sealed class SipEngine(IHttpClientFactory clients, IConfiguration configuration, ILogger<SipEngine> logger) : BackgroundService
{
    private sealed record Registration(SipAccount Account, SIPRegistrationUserAgent Agent, SIPEndPoint? Proxy);
    private readonly ConcurrentDictionary<string, Registration> accounts = new();
    private readonly ConcurrentDictionary<string, LiveCall> calls = new();
    private readonly SIPTransport transport = new();
    private readonly SemaphoreSlim configGate = new(1, 1);
    private string configHash = "";
    private CancellationToken stopping;
    private int nextPort = 20000;
    public object Health(string owner) => new { accounts = accounts.Values.Where(x => x.Account.Owner == owner).Select(x => new {
        x.Account.Id, x.Account.Name, registered = x.Agent.IsRegistered, state = x.Agent.IsRegistered ? "Registered" : "Registering",
        lastAttempt = x.Agent.LastRegisterAttemptAt, activeCalls = calls.Values.Count(c => c.Request.Owner == owner && c.Request.AccountId == x.Account.Id)
    }) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        stopping = ct;
        var port = int.TryParse(configuration["SIP_LISTEN_PORT"], out var p) ? p : 5060;
        transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, port)));
        transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, port)));
        transport.AddSIPChannel(new SIPTLSChannel(new IPEndPoint(IPAddress.Any, port + 1)));
        if (IPAddress.TryParse(configuration["SIP_ADVERTISE_ADDRESS"], out var address)) transport.ContactHost = address.ToString();
        transport.SIPTransportRequestReceived += Incoming;
        while (!ct.IsCancellationRequested)
        {
            try { await Refresh(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { logger.LogWarning("SIP configuration unavailable: {Type}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private async Task Refresh(CancellationToken ct)
    {
        using var client = clients.CreateClient("core");
        var configured = await client.GetFromJsonAsync<List<SipAccount>>("/internal/sip/config", ct) ?? [];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configured)));
        if (hash == configHash) return;
        await configGate.WaitAsync(ct);
        try
        {
            // Configuration changes terminate affected sessions; never retain removed credentials indefinitely.
            foreach (var call in calls.Values) call.Stop();
            foreach (var existing in accounts.Values) existing.Agent.Stop();
            accounts.Clear();
            foreach (var account in configured.Where(a => a.Enabled))
            {
                account.Validate();
                var scheme = account.UseTls ? "sips" : "sip";
                var registrar = $"{scheme}:{account.Server}:{account.Port};transport={account.Transport.ToLowerInvariant()}";
                var aor = SIPURI.ParseSIPURI($"{scheme}:{account.Username}@{(account.Domain.Length > 0 ? account.Domain : account.Server)};transport={account.Transport.ToLowerInvariant()}");
                SIPEndPoint? proxy = null;
                if (account.OutboundProxy.Length > 0) proxy = await transport.ResolveSIPUriAsync(SIPURI.ParseSIPURI(account.OutboundProxy));
                var contact = SIPURI.ParseSIPURI($"{scheme}:{account.Username}@0.0.0.0;transport={account.Transport.ToLowerInvariant()}");
                var registration = new SIPRegistrationUserAgent(transport, proxy!, aor,
                    string.IsNullOrEmpty(account.AuthId) ? account.Username : account.AuthId, account.Password, null!, registrar, contact,
                    account.RegistrationInterval, [], exitOnUnequivocalFailure: false);
                accounts[account.Owner + ":" + account.Id] = new(account, registration, proxy);
                registration.Start();
            }
            configHash = hash;
        }
        finally { configGate.Release(); }
    }

    public object Dial(SipDial request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Number, @"^\+[1-9]\d{6,14}$") || request.MaxDuration is < 30 or > 1800)
            throw new JarvisException("Invalid destination or duration.");
        if (!accounts.TryGetValue(request.Owner + ":" + request.AccountId, out var account) || !account.Agent.IsRegistered || !account.Account.AllowedDirections.Contains("outbound"))
            throw new JarvisException("SIP account is not registered or authorized.", 409);
        if (calls.ContainsKey(request.CallId)) return new { request.CallId, status = "Calling" };
        if (calls.Count >= 8) throw new JarvisException("Concurrent call limit reached.", 429);
        var call = Create(request, account);
        if (!calls.TryAdd(request.CallId, call)) { call.Dispose(); throw new JarvisException("Call already exists.", 409); }
        _ = Run(call, account, null);
        return new { request.CallId, status = "Calling" };
    }

    private LiveCall Create(SipDial request, Registration account)
    {
        var port = Interlocked.Add(ref nextPort, 2);
        if (port > 20096) { Interlocked.Exchange(ref nextPort, 20000); port = Interlocked.Add(ref nextPort, 2); }
        var media = new AdvertisedRtpSession(new RtpSessionConfig { IsMediaMultiplexed = false, IsRtcpMultiplexed = false,
            BindAddress = IPAddress.Any, BindPort = port, RtpSecureMediaOption = account.Account.Srtp ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation : RtpSecureMediaOptionEnum.None },
            IPAddress.TryParse(configuration["SIP_ADVERTISE_ADDRESS"],out var advertised)?advertised:null);
        media.addTrack(new MediaStreamTrack(SDPWellKnownMediaFormatsEnum.PCMU, SDPWellKnownMediaFormatsEnum.PCMA));
        var ua = new SIPUserAgent(transport, account.Proxy!, false);
        var call = new LiveCall(request, ua, media, configuration, stopping);
        return call;
    }

    private async Task Run(LiveCall call, Registration registration, SIPRequest? incoming)
    {
        var state = "Failed";
        try
        {
            call.UserAgent.ClientCallRinging += (_, _) => { _ = Event(call, "Ringing"); };
            bool connected;
            if (incoming is null)
            {
                var a = registration.Account;
                var target = Destination(a, call.Request.Number);
                var from = $"\"{a.DisplayName}\" <sip:{(a.CallerId.Length > 0 ? a.CallerId : a.Username)}@{(a.Domain.Length > 0 ? a.Domain : a.Server)}>";
                var descriptor = new SIPCallDescriptor(a.Username, a.Password, target, from, target, null!, [], a.AuthId.Length > 0 ? a.AuthId : a.Username,
                    SIPCallDirection.Out, "application/sdp", null!, IPAddress.TryParse(configuration["SIP_ADVERTISE_ADDRESS"], out var ip) ? ip : null!);
                connected = await call.UserAgent.Call(descriptor, call.Media, 30).WaitAsync(call.Token);
            }
            else connected = await call.UserAgent.Answer(call.UserAgent.AcceptCall(incoming), call.Media).WaitAsync(call.Token);
            if (!connected) return;
            await Event(call, "Connected");
            await call.BridgeAsync();
            state = "Completed";
        }
        catch (OperationCanceledException) { state = "Completed"; }
        catch (Exception e) { logger.LogWarning("Call {CallId} failed: {ErrorType}", call.Request.CallId, e.GetType().Name); }
        finally
        {
            call.Stop();
            try { await Event(call, state); } catch { logger.LogWarning("Final call state delivery failed: {CallId}", call.Request.CallId); }
            calls.TryRemove(call.Request.CallId, out _); call.Dispose();
        }
    }

    private async Task Incoming(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.INVITE || request.Header.To.ToTag is not null) return;
        var account = accounts.Values.FirstOrDefault(a => a.Account.Username == request.URI.User &&
            a.Account.AllowedDirections.Contains("inbound") && a.Account.TrustedPeers.Contains(remote.Address.ToString()));
        if (account is null || calls.Count >= 8) { await transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Forbidden, null)); return; }
        try
        {
            using var client = clients.CreateClient("core");
            using var result = await client.PostAsJsonAsync("/internal/sip/incoming", new { owner = account.Account.Owner, accountId = account.Account.Id, number = request.Header.From.FromURI.User }, stopping);
            if (!result.IsSuccessStatusCode) { await transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null)); return; }
            var approval = await result.Content.ReadFromJsonAsync<SipDial>(stopping) ?? throw new InvalidOperationException();
            var call = Create(approval, account);
            if (!calls.TryAdd(approval.CallId, call)) { call.Dispose(); return; }
            _ = Run(call, account, request);
        }
        catch { await transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.ServiceUnavailable, null)); }
    }

    private static string Destination(SipAccount a, string number) => $"{(a.UseTls ? "sips" : "sip")}:{number}@{a.Server}:{a.Port};transport={a.Transport.ToLowerInvariant()}";
    private async Task Event(LiveCall call, string state)
    {
        using var client = clients.CreateClient("core");
        using var response = await client.PostAsJsonAsync("/internal/sip/events", new { owner = call.Request.Owner, callId = call.Request.CallId, state }, CancellationToken.None);
        response.EnsureSuccessStatusCode();
    }
    public async Task<object> Control(string id, string operation, JsonObject args, CancellationToken ct)
    {
        if (!calls.TryGetValue(id, out var call) || call.Request.Owner != args["owner"]?.GetValue<string>()) throw new JarvisException("Call not active.", 404);
        switch (operation)
        {
            case "Hangup": call.Stop(); break;
            case "Hold": call.UserAgent.PutOnHold(); await Event(call, "OnHold"); break;
            case "Resume": call.UserAgent.TakeOffHold(); await Event(call, "Connected"); break;
            case "SendDtmf":
                var digits = args["digits"]?.GetValue<string>() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(digits, "^[0-9*#]{1,30}$")) throw new JarvisException("Invalid DTMF.");
                foreach (var digit in digits) { ct.ThrowIfCancellationRequested(); await call.UserAgent.SendDtmf(digit == '*' ? (byte)10 : digit == '#' ? (byte)11 : (byte)(digit - '0')); }
                break;
            case "Transfer":
                var number = args["number"]?.GetValue<string>() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^\+[1-9]\d{6,14}$")) throw new JarvisException("Invalid transfer target.");
                var account = accounts[call.Request.Owner + ":" + call.Request.AccountId].Account;
                await Event(call, "Transferring");
                if (!await call.UserAgent.BlindTransfer(SIPURI.ParseSIPURI(Destination(account, number)), TimeSpan.FromSeconds(15), ct)) { await Event(call, "Connected"); throw new JarvisException("REFER not accepted.", 409); }
                call.Stop(); break;
            default: throw new JarvisException("Unknown operation.");
        }
        return new { ok = true };
    }
    public override void Dispose()
    {
        foreach (var call in calls.Values) call.Stop();
        foreach (var account in accounts.Values) account.Agent.Stop();
        transport.Shutdown(); base.Dispose();
    }
}
