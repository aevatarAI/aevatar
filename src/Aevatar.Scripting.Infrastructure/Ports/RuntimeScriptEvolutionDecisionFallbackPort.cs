using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionDecisionFallbackPort : IScriptEvolutionDecisionFallbackPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _decisionTimeout;

    public RuntimeScriptEvolutionDecisionFallbackPort(
        RuntimeScriptActorAccessor actorAccessor,
        IStreamProvider streams,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _decisionTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionDecisionTimeout();
    }

    public async Task<ScriptPromotionDecision?> TryResolveAsync(
        string managerActorId,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var managerActor = await _actorAccessor.GetAsync(managerActorId);
        if (managerActor == null)
            return null;

        ScriptEvolutionDecisionRespondedEvent? response;
        try
        {
            response = await EventStreamQueryReplyAwaiter.QueryActorAsync<ScriptEvolutionDecisionRespondedEvent>(
                _streams,
                managerActor,
                ScriptingQueryRouteConventions.EvolutionReplyStreamPrefix,
                _decisionTimeout,
                (requestId, replyStreamId) => BuildQueryEnvelope(managerActorId, proposalId, requestId, replyStreamId),
                static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
                ScriptingQueryRouteConventions.BuildEvolutionDecisionTimeoutMessage,
                ct);
        }
        catch (TimeoutException)
        {
            return null;
        }

        if (response == null || !response.Found)
            return null;

        return MapDecision(response);
    }

    private static EventEnvelope BuildQueryEnvelope(
        string targetActorId,
        string proposalId,
        string requestId,
        string replyStreamId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new QueryScriptEvolutionDecisionRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                ProposalId = proposalId,
            }),
            PublisherId = ScriptingQueryChannels.EvolutionPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
            CorrelationId = proposalId,
        };
    }

    private static ScriptPromotionDecision MapDecision(ScriptEvolutionDecisionRespondedEvent response)
    {
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: response.Accepted,
            Diagnostics: response.Diagnostics.ToArray());
        return new ScriptPromotionDecision(
            Accepted: response.Accepted,
            ProposalId: response.ProposalId ?? string.Empty,
            ScriptId: response.ScriptId ?? string.Empty,
            BaseRevision: response.BaseRevision ?? string.Empty,
            CandidateRevision: response.CandidateRevision ?? string.Empty,
            Status: response.Status ?? string.Empty,
            FailureReason: response.FailureReason ?? string.Empty,
            DefinitionActorId: response.DefinitionActorId ?? string.Empty,
            CatalogActorId: response.CatalogActorId ?? string.Empty,
            ValidationReport: validation);
    }
}
