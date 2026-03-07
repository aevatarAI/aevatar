using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    private async Task PersistExecutionPlanAsync(
        ScriptEvolutionExecutionPlan executionPlan,
        CancellationToken ct)
    {
        foreach (var evt in executionPlan.DomainEvents)
            await PersistDomainEventAsync(evt, ct);
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
