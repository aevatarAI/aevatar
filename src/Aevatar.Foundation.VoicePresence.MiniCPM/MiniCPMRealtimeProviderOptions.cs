namespace Aevatar.Foundation.VoicePresence.MiniCPM;

/// <summary>
/// Runtime options for the MiniCPM voice provider adapter.
/// </summary>
public sealed class MiniCPMRealtimeProviderOptions
{
    public const int DefaultInputSampleRateHz = 16000;
    public const string DefaultStreamPath = "/api/v1/stream";
    public const string DefaultCompletionsPath = "/api/v1/completions";
    public const string DefaultStopPath = "/api/v1/stop";
    public const string DefaultUidHeaderName = "uid";

    public int EventQueueCapacity { get; init; } = 128;

    public int SupportedInputSampleRateHz { get; init; } = DefaultInputSampleRateHz;

    public string StreamPath { get; init; } = DefaultStreamPath;

    public string CompletionsPath { get; init; } = DefaultCompletionsPath;

    public string StopPath { get; init; } = DefaultStopPath;

    public string UidHeaderName { get; init; } = DefaultUidHeaderName;
}
