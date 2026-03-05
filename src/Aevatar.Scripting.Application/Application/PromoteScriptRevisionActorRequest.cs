namespace Aevatar.Scripting.Application;

public sealed record PromoteScriptRevisionActorRequest(
    string ScriptId,
    string Revision,
    string DefinitionActorId,
    string SourceHash,
    string ProposalId,
    string ExpectedBaseRevision);
