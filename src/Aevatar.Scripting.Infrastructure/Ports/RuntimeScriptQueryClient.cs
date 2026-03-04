using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptQueryClient
{
    private readonly IStreamProvider _streams;

    public RuntimeScriptQueryClient(IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public Task<TResponse> QueryActorAsync<TResponse>(
        IActor actor,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
        where TResponse : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamPrefix);
        ArgumentNullException.ThrowIfNull(envelopeFactory);
        ArgumentNullException.ThrowIfNull(isMatch);
        ArgumentNullException.ThrowIfNull(timeoutMessageFactory);

        return EventStreamQueryReplyAwaiter.QueryActorAsync<TResponse>(
            _streams,
            actor,
            replyStreamPrefix,
            timeout,
            envelopeFactory,
            isMatch,
            timeoutMessageFactory,
            ct);
    }
}
