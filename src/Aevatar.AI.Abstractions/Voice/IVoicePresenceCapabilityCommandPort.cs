using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.AI.Abstractions.Voice;

public interface IVoicePresenceCapabilityCommandPort
{
    Task<VoicePresenceCapabilityEnableReceipt> EnableAsync(
        string actorId,
        VoicePresenceEnableRequested request,
        CancellationToken ct = default);
}

public sealed record VoicePresenceCapabilityEnableReceipt(
    string ActorId,
    string ModuleName,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AcceptedAtUtc);

public static class VoicePresenceEnableRequests
{
    public const string DefaultModuleName = "voice_presence";
    public const int DefaultPcmSampleRateHz = 24000;

    public static VoicePresenceEnableRequested Normalize(VoicePresenceEnableRequested? request)
    {
        var normalized = request?.Clone() ?? new VoicePresenceEnableRequested();
        normalized.ModuleName = NormalizeModuleName(normalized.ModuleName);
        if (normalized.PcmSampleRateHz <= 0)
            normalized.PcmSampleRateHz = DefaultPcmSampleRateHz;
        if (normalized.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            normalized.RemoteAudioSupport = VoiceRemoteAudioSupport.Supported;

        return normalized;
    }

    public static string NormalizeModuleName(string? moduleName)
    {
        var normalized = moduleName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? DefaultModuleName : normalized;
    }

    public static bool HasSessionDefaults(VoicePresenceEnableRequested request) =>
        request.SessionDefaults is { } defaults && HasSessionDefaults(defaults);

    public static bool HasSessionDefaults(VoiceSessionDefaults defaults) =>
        defaults.HasVoice ||
        defaults.HasInstructions ||
        defaults.HasSampleRateHz ||
        defaults.HasTurnDetectionMode ||
        defaults.HasVadDetectionThreshold ||
        defaults.HasVadPrefixPaddingMs ||
        defaults.HasVadSilenceDurationMs;

    public static VoiceSessionConfig ToSessionConfig(VoicePresenceEnableRequested request)
    {
        var session = new VoiceSessionConfig
        {
            SampleRateHz = request.PcmSampleRateHz > 0 ? request.PcmSampleRateHz : DefaultPcmSampleRateHz,
        };
        if (request.SessionDefaults is not { } defaults)
            return session;

        if (defaults.HasVoice)
            session.Voice = defaults.Voice?.Trim() ?? string.Empty;
        if (defaults.HasInstructions)
            session.Instructions = defaults.Instructions ?? string.Empty;
        if (defaults.HasSampleRateHz)
            session.SampleRateHz = defaults.SampleRateHz;
        if (defaults.HasTurnDetectionMode)
            session.TurnDetectionMode = defaults.TurnDetectionMode;
        if (defaults.HasVadDetectionThreshold)
            session.VadDetectionThreshold = defaults.VadDetectionThreshold;
        if (defaults.HasVadPrefixPaddingMs)
            session.VadPrefixPaddingMs = defaults.VadPrefixPaddingMs;
        if (defaults.HasVadSilenceDurationMs)
            session.VadSilenceDurationMs = defaults.VadSilenceDurationMs;

        return session;
    }
}
