namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// Runtime options for the WebRTC voice transport.
/// </summary>
public sealed class WebRtcVoiceTransportOptions
{
    public const int DefaultPcmSampleRateHz = 24000;
    public const int DefaultFrameDurationMs = 20;
    public const int DefaultPendingSendFrameCapacity = 64;
    public const string DefaultControlDataChannelLabel = "voice-control";

    public int PcmSampleRateHz { get; init; } = DefaultPcmSampleRateHz;

    public int FrameDurationMs { get; init; } = DefaultFrameDurationMs;

    public int PendingSendFrameCapacity { get; init; } = DefaultPendingSendFrameCapacity;

    public string ControlDataChannelLabel { get; init; } = DefaultControlDataChannelLabel;

    public TimeSpan IceGatheringTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
