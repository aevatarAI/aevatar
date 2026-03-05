using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions.Streaming;

public interface IStreamRequestReplyClient
{
    Task<TResponse> QueryAsync<TResponse>(
        IStreamProvider streams,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, Task> dispatchAsync,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct = default)
        where TResponse : IMessage, new();

    Task<TResponse> QueryActorAsync<TResponse>(
        IStreamProvider streams,
        IActor actor,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct = default)
        where TResponse : IMessage, new();
}
