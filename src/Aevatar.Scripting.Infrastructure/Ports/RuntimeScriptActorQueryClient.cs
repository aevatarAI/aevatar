using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptActorQueryClient
{
    private readonly IStreamProvider _streams;
    private readonly IStreamRequestReplyClient _requestReplyClient;
    private readonly IActorDispatchPort _dispatchPort;

    public RuntimeScriptActorQueryClient(
        IStreamProvider streams,
        IStreamRequestReplyClient requestReplyClient,
        IActorDispatchPort dispatchPort)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _requestReplyClient = requestReplyClient ?? throw new ArgumentNullException(nameof(requestReplyClient));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task<TResponse> QueryActorAsync<TResponse>(
        string actorId,
        string replyStreamPrefix,
        TimeSpan timeout,
        Func<string, string, EventEnvelope> envelopeFactory,
        Func<TResponse, string, bool> isMatch,
        Func<string, string> timeoutMessageFactory,
        CancellationToken ct)
        where TResponse : IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamPrefix);
        ArgumentNullException.ThrowIfNull(envelopeFactory);
        ArgumentNullException.ThrowIfNull(isMatch);
        ArgumentNullException.ThrowIfNull(timeoutMessageFactory);

        return _requestReplyClient.QueryActorAsync<TResponse>(
            _streams,
            actorId,
            _dispatchPort,
            replyStreamPrefix,
            timeout,
            envelopeFactory,
            isMatch,
            timeoutMessageFactory,
            ct);
    }
}
