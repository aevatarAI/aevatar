namespace Aevatar.Scripting.Application;

public sealed record RollbackScriptRevisionCommand(
    string ScriptId,
    string TargetRevision,
    string Reason,
    string ProposalId);
