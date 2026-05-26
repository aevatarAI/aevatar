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
    Timestamp? LeaseExpiresAt = null);

// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
// Refactor helper, no behavior change: common disposable physical provider-session surface returned by provider connectors.
public abstract class RealtimeVoiceProviderSession : IAsyncDisposable
{
    public abstract Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);

    public abstract Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct);

    public abstract Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct);

    public abstract Task CancelResponseAsync(CancellationToken ct);

    public abstract Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct);

    public abstract ValueTask DisposeAsync();
}

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
        CancellationToken ct)
    {
        OnEvent = (providerEvent, token) => eventSink(sessionKey, providerEvent, token);
        return ConnectLegacySessionAsync(config, ct);
    }

    /// <summary>
    /// Connects the provider transport using the supplied provider configuration.
    /// </summary>
    Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct);

    /// <summary>
    /// Sends one PCM16 audio frame to the provider.
    /// </summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);

    /// <summary>
    /// Sends one tool result back to the provider conversation.
    /// </summary>
    Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct);

    /// <summary>
    /// Injects one external event into the provider conversation as structured context.
    /// </summary>
    Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct);

    /// <summary>
    /// Cancels the current response generation.
    /// </summary>
    Task CancelResponseAsync(CancellationToken ct);

    /// <summary>
    /// Updates session-scoped provider settings.
    /// </summary>
    Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct);

    /// <summary>
    /// Provider-to-module event callback.
    /// </summary>
    Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { set; }

    private async Task<RealtimeVoiceProviderSession> ConnectLegacySessionAsync(
        VoiceProviderConfig config,
        CancellationToken ct)
    {
        await ConnectAsync(config, ct);
        return new LegacyRealtimeVoiceProviderSession(this);
    }

    // Refactor helper, no behavior change: adapts older test/provider implementations to the disposable session shape.
    private sealed class LegacyRealtimeVoiceProviderSession(IRealtimeVoiceProvider provider) : RealtimeVoiceProviderSession
    {
        public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
            provider.SendAudioAsync(pcm16, ct);

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) =>
            provider.SendToolResultAsync(callId, resultJson, ct);

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) =>
            provider.InjectEventAsync(injection, ct);

        public override Task CancelResponseAsync(CancellationToken ct) =>
            provider.CancelResponseAsync(ct);

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) =>
            provider.UpdateSessionAsync(session, ct);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
