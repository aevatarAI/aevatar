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
    private readonly TimeSpan _commandAckTimeout;
    private readonly StartScriptEvolutionSessionActorRequestAdapter _startSessionAdapter = new();

    public RuntimeScriptEvolutionLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _commandAckTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionCommandAckTimeout();
    }

    public async Task<ScriptEvolutionCommandAccepted> ProposeAsync(
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

        var accepted = await _queryClient.QueryAsync<ScriptEvolutionCommandAcceptedEvent>(
            ScriptingQueryRouteConventions.EvolutionReplyStreamPrefix,
            _commandAckTimeout,
            (requestId, replyStreamId) => sessionActor.HandleEventAsync(
                _startSessionAdapter.Map(
                    new StartScriptEvolutionSessionActorRequest(
                        ProposalId: normalizedProposal.ProposalId ?? string.Empty,
                        ScriptId: normalizedProposal.ScriptId ?? string.Empty,
                        BaseRevision: normalizedProposal.BaseRevision ?? string.Empty,
                        CandidateRevision: normalizedProposal.CandidateRevision ?? string.Empty,
                        CandidateSource: normalizedProposal.CandidateSource ?? string.Empty,
                        CandidateSourceHash: normalizedProposal.CandidateSourceHash ?? string.Empty,
                        Reason: normalizedProposal.Reason ?? string.Empty,
                        RequestId: requestId,
                        ReplyStreamId: replyStreamId),
                    sessionActorId),
                ct),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildEvolutionCommandAckTimeoutMessage,
            ct);

        if (!accepted.Accepted)
        {
            var reason = string.IsNullOrWhiteSpace(accepted.FailureReason)
                ? $"Script evolution command rejected. proposal_id={normalizedProposalId}"
                : accepted.FailureReason;
            throw new InvalidOperationException(reason);
        }

        return new ScriptEvolutionCommandAccepted(
            accepted.ProposalId ?? normalizedProposalId,
            accepted.ScriptId ?? normalizedProposal.ScriptId,
            accepted.SessionActorId ?? sessionActorId);
    }
}
