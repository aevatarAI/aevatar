namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptEvolutionCommandAccepted(
    string ProposalId,
    string ScriptId,
    string SessionActorId);
