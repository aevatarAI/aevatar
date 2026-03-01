namespace Aevatar.Scripting.Application;

public sealed record PromoteScriptRevisionCommand(
    string ScriptId,
    string Revision,
    string DefinitionActorId,
    string SourceHash,
    string ProposalId);
