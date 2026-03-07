using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent : GAgentBase<ScriptEvolutionSessionState>
{
    internal const string SessionStatusStarted = "session_started";
    internal const string StatusPromotionFailed = "promotion_failed";
    internal const string PromotionFailedFailureReasonTag = "[promotion_failed]";

    private readonly ScriptEvolutionExecutionCoordinator _executionCoordinator;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionSessionGAgent(
        ScriptEvolutionExecutionCoordinator executionCoordinator,
        IScriptingActorAddressResolver addressResolver)
    {
        _executionCoordinator = executionCoordinator ?? throw new ArgumentNullException(nameof(executionCoordinator));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartScriptEvolutionSessionRequested(StartScriptEvolutionSessionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proposal = NormalizeProposal(evt);
        EnsureProposalBinding(proposal);
        if (!string.IsNullOrWhiteSpace(State.ProposalId))
            return;

        await PersistDomainEventAsync(new ScriptEvolutionSessionStartedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
        });

        var executionPlan = await _executionCoordinator.ExecuteAsync(proposal, CancellationToken.None);
        await PersistExecutionPlanAsync(executionPlan, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleQueryScriptEvolutionDecisionRequested(QueryScriptEvolutionDecisionRequestedEvent evt) =>
        HandleDecisionQueryAsync(evt, CancellationToken.None);

    protected override ScriptEvolutionSessionState TransitionState(
        ScriptEvolutionSessionState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionSessionStartedEvent>(ApplySessionStarted)
            .On<ScriptEvolutionSessionCompletedEvent>(ApplySessionCompleted)
            .OrCurrent();

    private void EnsureProposalBinding(ScriptEvolutionProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(State.ProposalId))
            return;

        if (!string.Equals(State.ProposalId, proposal.ProposalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script evolution session actor '{Id}' is bound to proposal '{State.ProposalId}', but received '{proposal.ProposalId}'.");
        }
    }
}
