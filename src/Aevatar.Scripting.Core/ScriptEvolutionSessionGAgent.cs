using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

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

    protected override Task OnActivateAsync(CancellationToken ct) =>
        EnsureExecutionRequestedAsync(ct);

    [EventHandler]
    public async Task HandleStartScriptEvolutionSessionRequested(StartScriptEvolutionSessionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proposal = NormalizeProposal(evt);
        Logger.LogInformation(
            "Script evolution session start received. actor_id={ActorId} proposal_id={ProposalId} script_id={ScriptId} base_revision={BaseRevision} candidate_revision={CandidateRevision} request_id={RequestId} reply_stream_id={ReplyStreamId}",
            Id,
            proposal.ProposalId,
            proposal.ScriptId,
            proposal.BaseRevision,
            proposal.CandidateRevision,
            evt.RequestId,
            evt.ReplyStreamId);
        EnsureProposalBinding(proposal);
        if (!string.IsNullOrWhiteSpace(State.ProposalId))
        {
            Logger.LogInformation(
                "Script evolution session already bound. actor_id={ActorId} proposal_id={ProposalId} completed={Completed} status={Status}",
                Id,
                State.ProposalId,
                State.Completed,
                State.Status);
            await SendAcceptedResponseIfRequestedAsync(evt, State.ProposalId, CancellationToken.None);
            await EnsureExecutionRequestedAsync(CancellationToken.None);
            return;
        }

        await PersistDomainEventAsync(new ScriptEvolutionSessionStartedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
            CandidateSource = proposal.CandidateSource,
            CandidateSourceHash = proposal.CandidateSourceHash,
            Reason = proposal.Reason,
        });
        await SendAcceptedResponseIfRequestedAsync(evt, proposal.ProposalId, CancellationToken.None);
        await EnsureExecutionRequestedAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleScriptEvolutionSessionExecutionRequested(ScriptEvolutionSessionExecutionRequestedEvent evt) =>
        HandleExecutionRequestedAsync(evt, CancellationToken.None);

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleScriptEvolutionSessionExecutionPlanReady(ScriptEvolutionSessionExecutionPlanReadyEvent evt) =>
        HandleExecutionPlanReadyAsync(evt, CancellationToken.None);

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
