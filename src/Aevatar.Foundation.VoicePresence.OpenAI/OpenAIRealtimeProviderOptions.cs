namespace Aevatar.Foundation.VoicePresence.OpenAI;

/// <summary>
/// Runtime options for the OpenAI realtime voice provider.
/// </summary>
public sealed class OpenAIRealtimeProviderOptions
{
    public const string DefaultModelName = "gpt-realtime";
    public const int DefaultSampleRateHz = 24000;

    public int EventQueueCapacity { get; init; } = 128;

    public string DefaultModel { get; init; } = DefaultModelName;

    public int SupportedSampleRateHz { get; init; } = DefaultSampleRateHz;

    public bool EnableServerVad { get; init; } = true;

    public float DetectionThreshold { get; init; } = 0.7f;

    public TimeSpan PrefixPadding { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan SilenceDuration { get; init; } = TimeSpan.FromMilliseconds(600);

    public bool InterruptResponseOnSpeech { get; init; } = true;

    public bool AutoCreateResponse { get; init; } = true;
}
