using System.Net;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Jarvis.Tests;

public class SipLoopbackTests
{
    [Theory]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMU, false)]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMA, false)]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMU, true)]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMA, true)]
    public async Task ActualSipInviteRtpAndHangup(SDPWellKnownMediaFormatsEnum codec, bool secure)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = cancellation.Token;
        using var callerTransport = new SIPTransport(); using var receiverTransport = new SIPTransport();
        var callerChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        var receiverChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
        callerTransport.AddSIPChannel(callerChannel); receiverTransport.AddSIPChannel(receiverChannel);
        using var caller = new SIPUserAgent(callerTransport, null!); using var receiver = new SIPUserAgent(receiverTransport, null!);
        // SDES is exercised only on isolated loopback. Production configuration requires TLS for SDES.
        RtpSessionConfig MediaConfig() => new() { BindAddress = IPAddress.Loopback,
            RtpSecureMediaOption = secure ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation : RtpSecureMediaOptionEnum.None };
        using var sending = new RTPSession(MediaConfig());
        using var receiving = new RTPSession(MediaConfig());
        sending.addTrack(new MediaStreamTrack(codec)); receiving.addTrack(new MediaStreamTrack(codec));
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var packet = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hungup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dtmf = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnDtmfTone += (tone, _) => dtmf.TrySetResult(tone);
        receiver.OnIncomingCall += async (_, request) => {
            try { answered.TrySetResult(await receiver.Answer(receiver.AcceptCall(request), receiving)); }
            catch (Exception e) { answered.TrySetException(e); }
        };
        receiving.OnRtpPacketReceived += (_, type, rtp) => { if (type == SDPMediaTypesEnum.audio) packet.TrySetResult(rtp.Payload); };
        receiver.OnCallHungup += _ => hungup.TrySetResult(true);
        try {
            Assert.True(await caller.Call($"sip:test@127.0.0.1:{receiverChannel.Port}", null!, null!, sending, 5).WaitAsync(ct));
            Assert.True(await answered.Task.WaitAsync(ct));
            var payload = Enumerable.Repeat(codec == SDPWellKnownMediaFormatsEnum.PCMA ? (byte)0xd5 : (byte)0xff, 160).ToArray();
            sending.SendAudio(160, payload);
            Assert.Equal(payload, await packet.Task.WaitAsync(ct));
            await caller.SendDtmf(5).WaitAsync(ct);
            Assert.Equal((byte)5, await dtmf.Task.WaitAsync(ct));
            caller.Hangup(); Assert.True(await hungup.Task.WaitAsync(ct));
        } finally { caller.Hangup(); receiver.Hangup(); callerTransport.Shutdown(); receiverTransport.Shutdown(); }
    }
}
