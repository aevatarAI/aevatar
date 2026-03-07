using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    private bool _executionRequested;
    private bool _executionInProgress;

    private async Task EnsureExecutionRequestedAsync(CancellationToken ct)
    {
        if (!HasPendingExecution() || _executionRequested || _executionInProgress)
            return;

        _executionRequested = true;
        await PublishAsync(
            new ScriptEvolutionSessionExecutionRequestedEvent
            {
                ProposalId = State.ProposalId ?? string.Empty,
            },
            EventDirection.Self,
            ct);
    }

    private async Task HandleExecutionRequestedAsync(
        ScriptEvolutionSessionExecutionRequestedEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_executionInProgress ||
            !HasPendingExecution() ||
            !string.Equals(evt.ProposalId, State.ProposalId, StringComparison.Ordinal))
        {
            _executionRequested = false;
            return;
        }

        _executionRequested = false;
        _executionInProgress = true;
        var proposal = BuildProposalFromState();
        Logger.LogInformation(
            "Script evolution session execution scheduled. actor_id={ActorId} proposal_id={ProposalId} script_id={ScriptId} base_revision={BaseRevision} candidate_revision={CandidateRevision}",
            Id,
            proposal.ProposalId,
            proposal.ScriptId,
            proposal.BaseRevision,
            proposal.CandidateRevision);

        _ = LaunchExecutionPlanAsync(proposal);
        await Task.CompletedTask;
    }

    private async Task HandleExecutionPlanReadyAsync(
        ScriptEvolutionSessionExecutionPlanReadyEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!string.Equals(evt.ProposalId, State.ProposalId, StringComparison.Ordinal) || State.Completed)
        {
            _executionInProgress = false;
            return;
        }

        try
        {
            var executionPlan = new ScriptEvolutionExecutionPlan(UnpackExecutionPlan(evt));
            await PersistExecutionPlanAsync(executionPlan, ct);
            Logger.LogInformation(
                "Script evolution session completed. actor_id={ActorId} proposal_id={ProposalId} accepted={Accepted} status={Status} failure_reason={FailureReason}",
                Id,
                State.ProposalId,
                State.Accepted,
                State.Status,
                State.FailureReason);
            await SendPendingDecisionResponseIfRequestedAsync(ct);
        }
        finally
        {
            _executionInProgress = false;
        }
    }

    private bool HasPendingExecution() =>
        !State.Completed &&
        !string.IsNullOrWhiteSpace(State.ProposalId) &&
        !string.IsNullOrWhiteSpace(State.ScriptId) &&
        !string.IsNullOrWhiteSpace(State.CandidateRevision) &&
        !string.IsNullOrWhiteSpace(State.CandidateSource);

    private async Task LaunchExecutionPlanAsync(ScriptEvolutionProposal proposal)
    {
        try
        {
            var executionPlan = await _executionCoordinator.ExecuteAsync(proposal, CancellationToken.None);
            await PublishAsync(
                BuildExecutionPlanReadyEvent(proposal.ProposalId, executionPlan),
                EventDirection.Self,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Script evolution session background execution failed. actor_id={ActorId} proposal_id={ProposalId}",
                Id,
                proposal.ProposalId);

            var executionPlan = BuildUnexpectedFailureExecutionPlan(proposal, ex, _addressResolver);
            await PublishAsync(
                BuildExecutionPlanReadyEvent(proposal.ProposalId, executionPlan),
                EventDirection.Self,
                CancellationToken.None);
        }
    }
}
