namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
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
        next.PendingRequestId = evt.RequestId ?? string.Empty;
        next.PendingReplyStreamId = evt.ReplyStreamId ?? string.Empty;
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
        next.CandidateSource = string.Empty;
        next.CandidateSourceHash = string.Empty;
        next.Reason = string.Empty;
        next.Diagnostics.Clear();
        next.Diagnostics.Add(evt.Diagnostics);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-completed");
        return next;
    }
}
