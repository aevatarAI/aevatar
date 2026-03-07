using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    private async Task PersistExecutionPlanAsync(
        ScriptEvolutionExecutionPlan executionPlan,
        CancellationToken ct)
    {
        foreach (var evt in executionPlan.DomainEvents)
        {
            await PersistDomainEventAsync(evt, ct);

            if (evt is ScriptEvolutionSessionCompletedEvent completed)
                await PublishAsync(completed, EventDirection.Self, ct);
        }
    }

    private static ScriptEvolutionSessionExecutionPlanReadyEvent BuildExecutionPlanReadyEvent(
        string proposalId,
        ScriptEvolutionExecutionPlan executionPlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentNullException.ThrowIfNull(executionPlan);

        var ready = new ScriptEvolutionSessionExecutionPlanReadyEvent
        {
            ProposalId = proposalId,
        };
        foreach (var evt in executionPlan.DomainEvents)
            ready.DomainEvents.Add(Any.Pack(evt));
        return ready;
    }

    private static IReadOnlyList<IMessage> UnpackExecutionPlan(
        ScriptEvolutionSessionExecutionPlanReadyEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var events = new List<IMessage>(evt.DomainEvents.Count);
        foreach (var packed in evt.DomainEvents)
            events.Add(UnpackExecutionPlanEvent(packed));
        return events;
    }

    private static IMessage UnpackExecutionPlanEvent(Any packed)
    {
        ArgumentNullException.ThrowIfNull(packed);

        if (packed.Is(ScriptEvolutionProposedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionProposedEvent>();
        if (packed.Is(ScriptEvolutionBuildRequestedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionBuildRequestedEvent>();
        if (packed.Is(ScriptEvolutionValidatedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionValidatedEvent>();
        if (packed.Is(ScriptEvolutionPromotedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionPromotedEvent>();
        if (packed.Is(ScriptEvolutionRejectedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionRejectedEvent>();
        if (packed.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
            return packed.Unpack<ScriptEvolutionSessionCompletedEvent>();

        throw new InvalidOperationException(
            $"Unsupported script evolution execution plan event type `{packed.TypeUrl}`.");
    }

    private static ScriptEvolutionExecutionPlan BuildUnexpectedFailureExecutionPlan(
        ScriptEvolutionProposal proposal,
        Exception ex,
        IScriptingActorAddressResolver addressResolver)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(addressResolver);

        var definitionActorId = addressResolver.GetDefinitionActorId(proposal.ScriptId);
        var catalogActorId = addressResolver.GetCatalogActorId();
        return new ScriptEvolutionExecutionPlan(
        [
            new ScriptEvolutionProposedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                BaseRevision = proposal.BaseRevision,
                CandidateRevision = proposal.CandidateRevision,
                CandidateSourceHash = proposal.CandidateSourceHash,
                Reason = proposal.Reason,
            },
            new ScriptEvolutionBuildRequestedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
            },
            new ScriptEvolutionRejectedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                FailureReason = ex.Message,
            },
            BuildCompletedEvent(
                accepted: false,
                proposal,
                status: ScriptEvolutionStatuses.Rejected,
                failureReason: ex.Message,
                definitionActorId: definitionActorId,
                catalogActorId: catalogActorId,
                diagnostics: Array.Empty<string>()),
        ]);
    }

    private ScriptEvolutionProposal BuildProposalFromState()
    {
        if (string.IsNullOrWhiteSpace(State.ProposalId))
            throw new InvalidOperationException("ProposalId is required.");
        if (string.IsNullOrWhiteSpace(State.ScriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(State.CandidateRevision))
            throw new InvalidOperationException("CandidateRevision is required.");
        if (string.IsNullOrWhiteSpace(State.CandidateSource))
            throw new InvalidOperationException("CandidateSource is required.");

        return new ScriptEvolutionProposal(
            ProposalId: State.ProposalId,
            ScriptId: State.ScriptId,
            BaseRevision: State.BaseRevision ?? string.Empty,
            CandidateRevision: State.CandidateRevision,
            CandidateSource: State.CandidateSource,
            CandidateSourceHash: State.CandidateSourceHash ?? string.Empty,
            Reason: State.Reason ?? string.Empty);
    }

    internal static ScriptEvolutionProposal NormalizeProposal(StartScriptEvolutionSessionRequestedEvent evt)
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

    internal static ScriptEvolutionSessionCompletedEvent BuildCompletedEvent(
        bool accepted,
        ScriptEvolutionProposal proposal,
        string status,
        string failureReason,
        string definitionActorId,
        string catalogActorId,
        IReadOnlyList<string> diagnostics)
    {
        var completed = new ScriptEvolutionSessionCompletedEvent
        {
            ProposalId = proposal.ProposalId ?? string.Empty,
            Accepted = accepted,
            Status = status ?? string.Empty,
            FailureReason = failureReason ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            CatalogActorId = catalogActorId ?? string.Empty,
        };
        if (diagnostics is { Count: > 0 })
            completed.Diagnostics.Add(diagnostics);
        return completed;
    }

    internal static string TagPromotionFailedFailureReason(string failureReason)
    {
        var normalized = failureReason ?? string.Empty;
        return normalized.StartsWith(PromotionFailedFailureReasonTag, StringComparison.Ordinal)
            ? normalized
            : PromotionFailedFailureReasonTag + normalized;
    }
}
