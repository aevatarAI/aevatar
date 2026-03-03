using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionSessionGAgent : GAgentBase<ScriptEvolutionSessionState>
{
    private const string SessionStatusStarted = "session_started";

    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionSessionGAgent(IScriptingActorAddressResolver addressResolver)
    {
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartScriptEvolutionSessionRequested(StartScriptEvolutionSessionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var normalizedProposal = NormalizeProposal(evt);
        if (!string.IsNullOrWhiteSpace(State.ProposalId))
        {
            if (!string.Equals(State.ProposalId, normalizedProposal.ProposalId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Script evolution session actor '{Id}' is bound to proposal '{State.ProposalId}', but received '{normalizedProposal.ProposalId}'.");
            }

            return;
        }

        await PersistDomainEventAsync(new ScriptEvolutionSessionStartedEvent
        {
            ProposalId = normalizedProposal.ProposalId,
            ScriptId = normalizedProposal.ScriptId,
            BaseRevision = normalizedProposal.BaseRevision,
            CandidateRevision = normalizedProposal.CandidateRevision,
        });

        var managerActorId = _addressResolver.GetEvolutionManagerActorId();
        await SendToAsync(managerActorId, new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = normalizedProposal.ProposalId,
            ScriptId = normalizedProposal.ScriptId,
            BaseRevision = normalizedProposal.BaseRevision,
            CandidateRevision = normalizedProposal.CandidateRevision,
            CandidateSource = normalizedProposal.CandidateSource,
            CandidateSourceHash = normalizedProposal.CandidateSourceHash,
            Reason = normalizedProposal.Reason,
            CallbackActorId = Id,
            CallbackRequestId = normalizedProposal.ProposalId,
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleScriptEvolutionDecisionResponded(ScriptEvolutionDecisionRespondedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(State.ProposalId) ||
            !string.Equals(State.ProposalId, evt.ProposalId ?? string.Empty, StringComparison.Ordinal) ||
            State.Completed)
        {
            return;
        }

        var completed = new ScriptEvolutionSessionCompletedEvent
        {
            ProposalId = evt.ProposalId ?? string.Empty,
            Accepted = evt.Accepted,
            Status = evt.Status ?? string.Empty,
            FailureReason = evt.FailureReason ?? string.Empty,
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            CatalogActorId = evt.CatalogActorId ?? string.Empty,
            Diagnostics = { evt.Diagnostics },
        };

        await PersistDomainEventAsync(completed);
    }

    protected override ScriptEvolutionSessionState TransitionState(
        ScriptEvolutionSessionState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionSessionStartedEvent>(ApplySessionStarted)
            .On<ScriptEvolutionSessionCompletedEvent>(ApplySessionCompleted)
            .OrCurrent();

    private static ScriptEvolutionProposal NormalizeProposal(StartScriptEvolutionSessionRequestedEvent evt)
    {
        var proposalId = string.IsNullOrWhiteSpace(evt.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : evt.ProposalId;
        var scriptId = evt.ScriptId ?? string.Empty;
        var candidateRevision = evt.CandidateRevision ?? string.Empty;
        var candidateSource = evt.CandidateSource ?? string.Empty;

        if (string.IsNullOrWhiteSpace(scriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(candidateRevision))
            throw new InvalidOperationException("CandidateRevision is required.");
        if (string.IsNullOrWhiteSpace(candidateSource))
            throw new InvalidOperationException("CandidateSource is required.");

        return new ScriptEvolutionProposal(
            ProposalId: proposalId,
            ScriptId: scriptId,
            BaseRevision: evt.BaseRevision ?? string.Empty,
            CandidateRevision: candidateRevision,
            CandidateSource: candidateSource,
            CandidateSourceHash: evt.CandidateSourceHash ?? string.Empty,
            Reason: evt.Reason ?? string.Empty);
    }

    private static ScriptEvolutionSessionState ApplySessionStarted(
        ScriptEvolutionSessionState state,
        ScriptEvolutionSessionStartedEvent evt)
    {
        var next = state.Clone();
        next.ProposalId = evt.ProposalId ?? string.Empty;
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.BaseRevision = evt.BaseRevision ?? string.Empty;
        next.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        next.Completed = false;
        next.Accepted = false;
        next.Status = SessionStatusStarted;
        next.FailureReason = string.Empty;
        next.DefinitionActorId = string.Empty;
        next.CatalogActorId = string.Empty;
        next.Diagnostics.Clear();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-started");
        return next;
    }

    private static ScriptEvolutionSessionState ApplySessionCompleted(
        ScriptEvolutionSessionState state,
        ScriptEvolutionSessionCompletedEvent evt)
    {
        var next = state.Clone();
        next.Completed = true;
        next.Accepted = evt.Accepted;
        next.Status = evt.Status ?? string.Empty;
        next.FailureReason = evt.FailureReason ?? string.Empty;
        next.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        next.Diagnostics.Clear();
        next.Diagnostics.Add(evt.Diagnostics);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-completed");
        return next;
    }

}
