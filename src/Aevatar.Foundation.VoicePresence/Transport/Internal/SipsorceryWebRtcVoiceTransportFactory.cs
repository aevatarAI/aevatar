using SIPSorcery.Net;

namespace Aevatar.Foundation.VoicePresence.Transport.Internal;

internal sealed class SipsorceryWebRtcVoiceTransportFactory : IWebRtcVoiceTransportFactory
{
    public async Task<WebRtcVoiceTransportSession> CreateAsync(
        string remoteOfferSdp,
        WebRtcVoiceTransportOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteOfferSdp);
        ArgumentNullException.ThrowIfNull(options);

        var peer = await SipsorceryWebRtcVoicePeer.CreateAsync(remoteOfferSdp, options, ct);
        var transport = new WebRtcVoiceTransport(peer, options);
        return new WebRtcVoiceTransportSession(transport, peer.AnswerSdp, transport.Completion);
    }
}
