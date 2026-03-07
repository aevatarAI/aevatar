using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionLifecycleService
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _decisionTimeout;
    private readonly StartScriptEvolutionSessionActorRequestAdapter _startSessionAdapter = new();
    private readonly QueryScriptEvolutionDecisionRequestAdapter _queryDecisionAdapter = new();

    public RuntimeScriptEvolutionLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _decisionTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionDecisionTimeout();
    }

    public async Task<ScriptPromotionDecision> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var normalizedProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : proposal.ProposalId;
        var normalizedProposal = proposal with { ProposalId = normalizedProposalId };

        var sessionActorId = _addressResolver.GetEvolutionSessionActorId(normalizedProposalId);
        var sessionActor = await _actorAccessor.GetOrCreateAsync<ScriptEvolutionSessionGAgent>(
            sessionActorId,
            "Script evolution session actor not found",
            ct);

        await sessionActor.HandleEventAsync(
            _startSessionAdapter.Map(
                new StartScriptEvolutionSessionActorRequest(
                    ProposalId: normalizedProposal.ProposalId ?? string.Empty,
                    ScriptId: normalizedProposal.ScriptId ?? string.Empty,
                    BaseRevision: normalizedProposal.BaseRevision ?? string.Empty,
                    CandidateRevision: normalizedProposal.CandidateRevision ?? string.Empty,
                    CandidateSource: normalizedProposal.CandidateSource ?? string.Empty,
                    CandidateSourceHash: normalizedProposal.CandidateSourceHash ?? string.Empty,
                    Reason: normalizedProposal.Reason ?? string.Empty),
                sessionActorId),
            ct);

        var responded = await _queryClient.QueryActorAsync<ScriptEvolutionDecisionRespondedEvent>(
            sessionActor,
            ScriptingQueryRouteConventions.EvolutionReplyStreamPrefix,
            _decisionTimeout,
            (requestId, replyStreamId) => _queryDecisionAdapter.Map(
                sessionActorId,
                requestId,
                replyStreamId,
                normalizedProposalId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildEvolutionDecisionTimeoutMessage,
            ct);

        if (!responded.Found)
        {
            var reason = string.IsNullOrWhiteSpace(responded.FailureReason)
                ? $"Script evolution decision unavailable. proposal_id={normalizedProposalId}"
                : responded.FailureReason;
            throw new InvalidOperationException(reason);
        }

        return MapDecision(normalizedProposal, responded);
    }

    private static ScriptPromotionDecision MapDecision(
        ScriptEvolutionProposal proposal,
        ScriptEvolutionDecisionRespondedEvent completed)
    {
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: completed.Accepted,
            Diagnostics: completed.Diagnostics.ToArray());
        return new ScriptPromotionDecision(
            Accepted: completed.Accepted,
            ProposalId: proposal.ProposalId ?? string.Empty,
            ScriptId: proposal.ScriptId ?? string.Empty,
            BaseRevision: proposal.BaseRevision ?? string.Empty,
            CandidateRevision: proposal.CandidateRevision ?? string.Empty,
            Status: completed.Status ?? string.Empty,
            FailureReason: completed.FailureReason ?? string.Empty,
            DefinitionActorId: completed.DefinitionActorId ?? string.Empty,
            CatalogActorId: completed.CatalogActorId ?? string.Empty,
            ValidationReport: validation);
    }
}
