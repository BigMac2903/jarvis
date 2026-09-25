using System.Net;
using SIPSorcery.Net;

namespace Jarvis.Sip;

public sealed class AdvertisedRtpSession(RtpSessionConfig config, IPAddress? advertised) : RTPSession(config)
{
    public override SDP CreateOffer(IPAddress? connectionAddress = null) => base.CreateOffer(advertised ?? connectionAddress!);
    public override SDP CreateAnswer(IPAddress connectionAddress) => base.CreateAnswer(advertised ?? connectionAddress);
}
