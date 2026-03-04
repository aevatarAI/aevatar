namespace Aevatar.Scripting.Application;

public sealed record RollbackScriptRevisionActorRequest(
    string ScriptId,
    string TargetRevision,
    string Reason,
    string ProposalId,
    string ExpectedCurrentRevision);
