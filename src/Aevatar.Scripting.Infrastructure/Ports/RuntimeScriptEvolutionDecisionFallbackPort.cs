using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionDecisionFallbackPort : IScriptEvolutionDecisionFallbackPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _decisionTimeout;

    public RuntimeScriptEvolutionDecisionFallbackPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _decisionTimeout = NormalizeTimeout(timeouts.EvolutionDecisionTimeout);
    }

    public async Task<ScriptPromotionDecision?> TryResolveAsync(
        string managerActorId,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var managerActor = await _runtime.GetAsync(managerActorId);
        if (managerActor == null)
            return null;

        ScriptEvolutionDecisionRespondedEvent? response;
        try
        {
            response = await ScriptQueryReplyAwaiter.QueryAsync<ScriptEvolutionDecisionRespondedEvent>(
                _streams,
                "scripting.query.evolution.reply",
                _decisionTimeout,
                (requestId, replyStreamId) => managerActor.HandleEventAsync(
                    BuildQueryEnvelope(managerActorId, proposalId, requestId, replyStreamId),
                    ct),
                static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
                static requestId => $"Timeout waiting for script evolution decision query response. request_id={requestId}",
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
            PublisherId = "scripting.query.evolution",
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

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
