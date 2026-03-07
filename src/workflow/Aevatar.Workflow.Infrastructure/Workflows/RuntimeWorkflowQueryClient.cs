using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Workflow.Infrastructure.Workflows;

public sealed class RuntimeWorkflowQueryClient
{
    private readonly IStreamProvider _streams;
    private readonly IStreamRequestReplyClient _requestReplyClient;

    public RuntimeWorkflowQueryClient(
        IStreamProvider streams,
        IStreamRequestReplyClient requestReplyClient)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _requestReplyClient = requestReplyClient ?? throw new ArgumentNullException(nameof(requestReplyClient));
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

        return _requestReplyClient.QueryActorAsync<TResponse>(
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
