using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// Factory for creating a negotiated WebRTC voice transport from a remote SDP offer.
/// </summary>
public interface IWebRtcVoiceTransportFactory
{
    Task<WebRtcVoiceTransportSession> CreateAsync(
        string remoteOfferSdp,
        WebRtcVoiceTransportOptions options,
        CancellationToken ct);
}

/// <summary>
/// Result of creating one WebRTC voice transport.
/// </summary>
public sealed record WebRtcVoiceTransportSession(
    IVoiceTransport Transport,
    string AnswerSdp,
    Task Completion);
