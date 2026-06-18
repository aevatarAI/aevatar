using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Abstractions;

// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
public sealed record VoiceProviderSessionKey(
    string SessionId,
    string OwnerId,
    string TransportLeaseId,
    long LeaseEpoch,
    Timestamp? LeaseExpiresAt = null,
    string ActorId = "",
    string ModuleName = "",
    VoiceToolExecutionContext? ToolContext = null);

// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
public abstract class RealtimeVoiceProviderSession : IAsyncDisposable
{
    public abstract Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);

    public abstract Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct);

    public abstract Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct);

    public abstract Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct);

    public abstract Task CancelResponseAsync(CancellationToken ct);

    public abstract Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct);

    public abstract ValueTask DisposeAsync();
}

public sealed record VoiceProviderAudioFrame(
    ReadOnlyMemory<byte> Pcm16,
    int SampleRateHz,
    string ProviderResponseId);

/// <summary>
/// Realtime voice provider abstraction for voice-presence sessions.
/// </summary>
// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
public interface IRealtimeVoiceProvider : IAsyncDisposable
{
    Task<RealtimeVoiceProviderSession> ConnectAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct);
}
