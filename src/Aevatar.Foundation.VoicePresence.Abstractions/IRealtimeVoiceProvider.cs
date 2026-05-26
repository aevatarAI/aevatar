namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Realtime voice provider abstraction for voice-presence sessions.
/// </summary>
public interface IRealtimeVoiceProvider : IAsyncDisposable
{
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
}
