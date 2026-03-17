using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionSessionGAgent : GAgentBase<ScriptEvolutionSessionState>
{
    private const string SessionStatusStarted = "session_started";

    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly IScriptEvolutionPolicyEvaluator _policyEvaluator;
    private readonly IScriptEvolutionValidationService _validationService;
    private readonly IScriptCatalogBaselineReader _catalogBaselineReader;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly IScriptPromotionCompensationService _promotionCompensationService;
    private readonly IScriptEvolutionRollbackService _rollbackService;

    public ScriptEvolutionSessionGAgent(
        IScriptingActorAddressResolver addressResolver,
        IScriptEvolutionPolicyEvaluator policyEvaluator,
        IScriptEvolutionValidationService validationService,
        IScriptCatalogBaselineReader catalogBaselineReader,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptCatalogCommandPort catalogCommandPort,
        IScriptPromotionCompensationService promotionCompensationService,
        IScriptEvolutionRollbackService rollbackService)
    {
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _policyEvaluator = policyEvaluator ?? throw new ArgumentNullException(nameof(policyEvaluator));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _catalogBaselineReader = catalogBaselineReader ?? throw new ArgumentNullException(nameof(catalogBaselineReader));
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _promotionCompensationService = promotionCompensationService ?? throw new ArgumentNullException(nameof(promotionCompensationService));
        _rollbackService = rollbackService ?? throw new ArgumentNullException(nameof(rollbackService));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartScriptEvolutionSessionRequested(StartScriptEvolutionSessionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proposal = NormalizeProposal(evt);
        if (!string.IsNullOrWhiteSpace(State.ProposalId))
        {
            if (!string.Equals(State.ProposalId, proposal.ProposalId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Script evolution session actor '{Id}' is bound to proposal '{State.ProposalId}', but received '{proposal.ProposalId}'.");
            }

            return;
        }

        await PersistDomainEventAsync(new ScriptEvolutionSessionStartedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
            CandidateSourceHash = proposal.CandidateSourceHash,
            Reason = proposal.Reason,
            CandidateSource = proposal.CandidateSource,
        });

        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionProposedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
            CandidateSourceHash = proposal.CandidateSourceHash,
            Reason = proposal.Reason,
        });

        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionBuildRequestedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
        });

        await PublishAsync(
            new ScriptEvolutionExecutionRequestedEvent
            {
                ProposalId = proposal.ProposalId,
            },
            TopologyAudience.Self,
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleScriptEvolutionExecutionRequested(ScriptEvolutionExecutionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(State.ProposalId) ||
            !string.Equals(State.ProposalId, evt.ProposalId ?? string.Empty, StringComparison.Ordinal) ||
            State.Completed)
        {
            return;
        }

        try
        {
            await ExecuteProposalAsync(BuildProposalFromState(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Script evolution execution failed unexpectedly. session_actor_id={SessionActorId} proposal_id={ProposalId}",
                Id,
                State.ProposalId);

            if (State.Completed)
                return;

            var proposal = BuildBestEffortProposalFromState(evt.ProposalId);
            var definitionActorId = string.IsNullOrWhiteSpace(proposal.ScriptId)
                ? string.Empty
                : _addressResolver.GetDefinitionActorId(proposal.ScriptId);

            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision ?? string.Empty,
                ScriptEvolutionStatuses.PromotionFailed,
                "Unexpected script evolution execution failure. reason=" + ex.Message,
                [ex.GetType().Name],
                definitionActorId,
                _addressResolver.GetCatalogActorId(),
                CancellationToken.None);
        }
    }

    [EventHandler]
    public async Task HandleScriptEvolutionRollbackRequested(ScriptEvolutionRollbackRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(State.ProposalId) ||
            !string.Equals(State.ProposalId, evt.ProposalId ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = evt.ProposalId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            TargetRevision = evt.TargetRevision ?? string.Empty,
            Reason = evt.Reason ?? string.Empty,
            CatalogActorId = evt.CatalogActorId ?? string.Empty,
        });

        await _rollbackService.RollbackAsync(
            new ScriptRollbackRequest(
                ProposalId: evt.ProposalId ?? string.Empty,
                ScriptId: evt.ScriptId ?? string.Empty,
                TargetRevision: evt.TargetRevision ?? string.Empty,
                CatalogActorId: evt.CatalogActorId ?? string.Empty,
                Reason: evt.Reason ?? string.Empty,
                ExpectedCurrentRevision: string.Empty),
            CancellationToken.None);

        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionRolledBackEvent
        {
            ProposalId = evt.ProposalId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            TargetRevision = evt.TargetRevision ?? string.Empty,
            PreviousRevision = string.Empty,
            CatalogActorId = string.IsNullOrWhiteSpace(evt.CatalogActorId)
                ? _addressResolver.GetCatalogActorId()
                : evt.CatalogActorId,
        });
    }

    protected override ScriptEvolutionSessionState TransitionState(
        ScriptEvolutionSessionState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionSessionStartedEvent>(ApplySessionStarted)
            .On<ScriptEvolutionProposedEvent>(ApplyProposed)
            .On<ScriptEvolutionBuildRequestedEvent>(ApplyBuildRequested)
            .On<ScriptEvolutionValidatedEvent>(ApplyValidated)
            .On<ScriptEvolutionRejectedEvent>(ApplyRejected)
            .On<ScriptEvolutionPromotedEvent>(ApplyPromoted)
            .On<ScriptEvolutionRollbackRequestedEvent>(ApplyRollbackRequested)
            .On<ScriptEvolutionRolledBackEvent>(ApplyRolledBack)
            .On<ScriptEvolutionSessionCompletedEvent>(ApplySessionCompleted)
            .OrCurrent();

    private async Task ExecuteProposalAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        var defaultDefinitionActorId = _addressResolver.GetDefinitionActorId(proposal.ScriptId);
        var defaultCatalogActorId = _addressResolver.GetCatalogActorId();

        var policyFailure = _policyEvaluator.EvaluateFailure(proposal);
        if (!string.IsNullOrWhiteSpace(policyFailure))
        {
            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision,
                ScriptEvolutionStatuses.Rejected,
                policyFailure,
                Array.Empty<string>(),
                defaultDefinitionActorId,
                defaultCatalogActorId,
                ct);
            return;
        }

        var validation = await _validationService.ValidateAsync(proposal, ct);
        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionValidatedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
            IsValid = validation.IsSuccess,
            Diagnostics = { validation.Diagnostics },
        }, ct);

        if (!validation.IsSuccess)
        {
            var validationDiagnostics = validation.Diagnostics ?? Array.Empty<string>();
            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision ?? string.Empty,
                ScriptEvolutionStatuses.Rejected,
                string.Join("; ", validationDiagnostics),
                validationDiagnostics,
                defaultDefinitionActorId,
                defaultCatalogActorId,
                ct);
            return;
        }

        var baselineResolution = await _catalogBaselineReader.ReadAsync(proposal, ct);
        if (baselineResolution.HasFailure)
        {
            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision ?? string.Empty,
                ScriptEvolutionStatuses.PromotionFailed,
                baselineResolution.FailureReason,
                validation.Diagnostics ?? Array.Empty<string>(),
                defaultDefinitionActorId,
                baselineResolution.CatalogActorId,
                ct);
            return;
        }

        ScriptDefinitionUpsertResult definitionUpsert;
        try
        {
            definitionUpsert = await _definitionCommandPort.UpsertDefinitionWithSnapshotAsync(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty,
                proposal.CandidateSourceHash ?? string.Empty,
                null,
                ct);
        }
        catch (Exception ex)
        {
            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision,
                ScriptEvolutionStatuses.PromotionFailed,
                "Failed to upsert candidate definition before promotion. reason=" + ex.Message,
                validation.Diagnostics,
                defaultDefinitionActorId,
                baselineResolution.CatalogActorId,
                ct);
            return;
        }

        var definitionActorId = definitionUpsert.ActorId;

        try
        {
            await _catalogCommandPort.PromoteCatalogRevisionAsync(
                baselineResolution.CatalogActorId,
                proposal.ScriptId ?? string.Empty,
                proposal.BaseRevision ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                definitionActorId,
                proposal.CandidateSourceHash ?? string.Empty,
                proposal.ProposalId ?? string.Empty,
                ct);

            await PersistAndMirrorIndexEventAsync(new ScriptEvolutionPromotedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                DefinitionActorId = definitionActorId,
                CatalogActorId = baselineResolution.CatalogActorId,
            }, ct);

            await PersistDomainEventAsync(new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = proposal.ProposalId,
                Accepted = true,
                Status = ScriptEvolutionStatuses.Promoted,
                FailureReason = string.Empty,
                DefinitionActorId = definitionActorId,
                CatalogActorId = baselineResolution.CatalogActorId,
                Diagnostics = { validation.Diagnostics },
                DefinitionSnapshot = definitionUpsert.Snapshot.ToBindingSpec(),
            });
        }
        catch (Exception ex)
        {
            var compensation = await _promotionCompensationService.TryCompensateAsync(
                baselineResolution.CatalogActorId,
                proposal,
                baselineResolution.Baseline,
                ct);
            await RejectAndCompleteAsync(
                proposal,
                proposal.CandidateRevision ?? string.Empty,
                ScriptEvolutionStatuses.PromotionFailed,
                "Promotion failed after definition upsert. definition_actor_id=" +
                definitionActorId +
                " candidate_revision=" +
                (proposal.CandidateRevision ?? string.Empty) +
                " catalog_baseline_source=" +
                baselineResolution.BaselineSource +
                " reason=" +
                ex.Message +
                " compensation=" +
                compensation,
                validation.Diagnostics,
                definitionActorId,
                baselineResolution.CatalogActorId,
                ct);
        }
    }

    private async Task RejectAndCompleteAsync(
        ScriptEvolutionProposal proposal,
        string candidateRevision,
        string status,
        string failureReason,
        IReadOnlyList<string> diagnostics,
        string definitionActorId,
        string catalogActorId,
        CancellationToken ct)
    {
        await PersistAndMirrorIndexEventAsync(new ScriptEvolutionRejectedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = candidateRevision,
            FailureReason = failureReason ?? string.Empty,
            Status = status ?? string.Empty,
        }, ct);

        await PersistDomainEventAsync(new ScriptEvolutionSessionCompletedEvent
        {
            ProposalId = proposal.ProposalId,
            Accepted = false,
            Status = status ?? string.Empty,
            FailureReason = failureReason ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            CatalogActorId = catalogActorId ?? string.Empty,
            Diagnostics = { diagnostics },
        });
    }

    private async Task PersistAndMirrorIndexEventAsync<T>(
        T evt,
        CancellationToken ct = default)
        where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(evt);

        await PersistDomainEventAsync(evt);
        await TryMirrorIndexEventAsync(evt, ct);
    }

    private async Task TryMirrorIndexEventAsync<T>(
        T evt,
        CancellationToken ct)
        where T : IMessage
    {
        var managerActorId = _addressResolver.GetEvolutionManagerActorId();
        if (string.IsNullOrWhiteSpace(managerActorId))
            return;

        try
        {
            await SendToAsync(managerActorId, evt, ct);
        }
        catch
        {
            // Manager mirror is an index side-effect; proposal execution ownership remains on the session actor.
        }
    }

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

    private ScriptEvolutionProposal BuildProposalFromState()
    {
        if (string.IsNullOrWhiteSpace(State.ProposalId))
            throw new InvalidOperationException("ProposalId is required before executing script evolution.");
        if (string.IsNullOrWhiteSpace(State.ScriptId))
            throw new InvalidOperationException("ScriptId is required before executing script evolution.");
        if (string.IsNullOrWhiteSpace(State.CandidateRevision))
            throw new InvalidOperationException("CandidateRevision is required before executing script evolution.");
        if (string.IsNullOrWhiteSpace(State.CandidateSource))
            throw new InvalidOperationException("CandidateSource is required before executing script evolution.");

        return new ScriptEvolutionProposal(
            ProposalId: State.ProposalId,
            ScriptId: State.ScriptId,
            BaseRevision: State.BaseRevision ?? string.Empty,
            CandidateRevision: State.CandidateRevision,
            CandidateSource: State.CandidateSource,
            CandidateSourceHash: State.CandidateSourceHash ?? string.Empty,
            Reason: State.Reason ?? string.Empty);
    }

    private ScriptEvolutionProposal BuildBestEffortProposalFromState(string? fallbackProposalId)
    {
        var proposalId = string.IsNullOrWhiteSpace(State.ProposalId)
            ? fallbackProposalId ?? string.Empty
            : State.ProposalId;

        return new ScriptEvolutionProposal(
            ProposalId: proposalId,
            ScriptId: State.ScriptId ?? string.Empty,
            BaseRevision: State.BaseRevision ?? string.Empty,
            CandidateRevision: State.CandidateRevision ?? string.Empty,
            CandidateSource: State.CandidateSource ?? string.Empty,
            CandidateSourceHash: State.CandidateSourceHash ?? string.Empty,
            Reason: State.Reason ?? string.Empty);
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
        next.CandidateSource = evt.CandidateSource ?? string.Empty;
        next.CandidateSourceHash = evt.CandidateSourceHash ?? string.Empty;
        next.Reason = evt.Reason ?? string.Empty;
        next.Completed = false;
        next.Accepted = false;
        next.PolicyAllowed = false;
        next.ValidationSucceeded = false;
        next.Status = SessionStatusStarted;
        next.FailureReason = string.Empty;
        next.DefinitionActorId = string.Empty;
        next.CatalogActorId = string.Empty;
        next.Diagnostics.Clear();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-started");
        return next;
    }

    private static ScriptEvolutionSessionState ApplyProposed(
        ScriptEvolutionSessionState state,
        ScriptEvolutionProposedEvent evt)
    {
        var next = state.Clone();
        next.ProposalId = evt.ProposalId ?? string.Empty;
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.BaseRevision = evt.BaseRevision ?? string.Empty;
        next.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        next.CandidateSourceHash = evt.CandidateSourceHash ?? string.Empty;
        next.Reason = evt.Reason ?? string.Empty;
        next.PolicyAllowed = false;
        next.ValidationSucceeded = false;
        next.Status = ScriptEvolutionStatuses.Proposed;
        next.FailureReason = string.Empty;
        next.DefinitionActorId = string.Empty;
        next.CatalogActorId = string.Empty;
        next.Diagnostics.Clear();
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, ScriptEvolutionStatuses.Proposed);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyBuildRequested(
        ScriptEvolutionSessionState state,
        ScriptEvolutionBuildRequestedEvent evt)
    {
        var next = state.Clone();
        next.Status = ScriptEvolutionStatuses.BuildRequested;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, ScriptEvolutionStatuses.BuildRequested);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyValidated(
        ScriptEvolutionSessionState state,
        ScriptEvolutionValidatedEvent evt)
    {
        var next = state.Clone();
        next.PolicyAllowed = true;
        next.ValidationSucceeded = evt.IsValid;
        next.Diagnostics.Clear();
        next.Diagnostics.Add(evt.Diagnostics);
        next.Status = evt.IsValid
            ? ScriptEvolutionStatuses.Validated
            : ScriptEvolutionStatuses.ValidationFailed;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, next.Status);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyRejected(
        ScriptEvolutionSessionState state,
        ScriptEvolutionRejectedEvent evt)
    {
        var next = state.Clone();
        next.FailureReason = evt.FailureReason ?? string.Empty;
        next.Status = string.IsNullOrWhiteSpace(evt.Status)
            ? ScriptEvolutionStatuses.Rejected
            : evt.Status;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, next.Status);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyPromoted(
        ScriptEvolutionSessionState state,
        ScriptEvolutionPromotedEvent evt)
    {
        var next = state.Clone();
        next.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        next.FailureReason = string.Empty;
        next.Status = ScriptEvolutionStatuses.Promoted;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, next.Status);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyRollbackRequested(
        ScriptEvolutionSessionState state,
        ScriptEvolutionRollbackRequestedEvent evt)
    {
        var next = state.Clone();
        next.FailureReason = evt.Reason ?? string.Empty;
        next.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.TargetRevision))
            next.CandidateRevision = evt.TargetRevision;
        next.Status = ScriptEvolutionStatuses.RollbackRequested;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, next.Status);
        return next;
    }

    private static ScriptEvolutionSessionState ApplyRolledBack(
        ScriptEvolutionSessionState state,
        ScriptEvolutionRolledBackEvent evt)
    {
        var next = state.Clone();
        next.CandidateRevision = evt.TargetRevision ?? string.Empty;
        next.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        next.FailureReason = string.Empty;
        next.Status = ScriptEvolutionStatuses.RolledBack;
        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, next.Status);
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

    private static void StampAppliedEvent(
        ScriptEvolutionSessionState current,
        ScriptEvolutionSessionState next,
        string proposalId,
        string status)
    {
        next.LastAppliedEventVersion = current.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposalId, ":", status);
    }
}
