using Aevatar.Scripting.Application;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionDecisionFallbackPort : IScriptEvolutionDecisionFallbackPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly TimeSpan _decisionTimeout;
    public RuntimeScriptEvolutionDecisionFallbackPort(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _decisionTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionDecisionTimeout();
    }

    public async Task<ScriptPromotionDecision?> TryResolveAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var sessionActor = await _actorAccessor.GetAsync(sessionActorId);
        if (sessionActor == null)
            return null;

        ScriptEvolutionDecisionRespondedEvent? response;
        try
        {
            response = await _queryClient.QueryActorAsync<ScriptEvolutionDecisionRespondedEvent>(
                sessionActor,
                ScriptingQueryRouteConventions.EvolutionReplyStreamPrefix,
                _decisionTimeout,
                (requestId, replyStreamId) => ScriptingQueryEnvelopeFactory.CreateEvolutionDecisionQuery(
                    sessionActorId,
                    requestId,
                    replyStreamId,
                    proposalId),
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
