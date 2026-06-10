using OpenAI.Realtime;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Internal;

internal interface IOpenAIRealtimeSession : IAsyncDisposable
{
    Task SendSessionUpdateAsync(BinaryData sessionUpdateEvent, CancellationToken ct);

    Task SendInputAudioAsync(BinaryData audio, CancellationToken ct);

    Task SendInputImageAsync(BinaryData inputImageEvent, CancellationToken ct);

    Task AddItemAsync(RealtimeItem item, CancellationToken ct);

    Task StartResponseAsync(CancellationToken ct);

    Task CancelResponseAsync(CancellationToken ct);

    IAsyncEnumerable<OpenAIRealtimeSessionEvent> ReceiveEventsAsync(CancellationToken ct);
}
