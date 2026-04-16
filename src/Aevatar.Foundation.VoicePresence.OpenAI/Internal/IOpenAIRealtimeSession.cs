using OpenAI.Realtime;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Internal;

internal interface IOpenAIRealtimeSession : IAsyncDisposable
{
    Task ConfigureConversationSessionAsync(RealtimeConversationSessionOptions options, CancellationToken ct);

    Task SendInputAudioAsync(BinaryData audio, CancellationToken ct);

    Task AddItemAsync(RealtimeItem item, CancellationToken ct);

    Task StartResponseAsync(CancellationToken ct);

    Task CancelResponseAsync(CancellationToken ct);

    IAsyncEnumerable<OpenAIRealtimeSessionEvent> ReceiveEventsAsync(CancellationToken ct);
}
