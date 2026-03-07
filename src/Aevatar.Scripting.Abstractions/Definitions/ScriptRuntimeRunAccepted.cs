namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptRuntimeRunAccepted(
    string RuntimeActorId,
    string RunId,
    string DefinitionActorId,
    string ScriptRevision);
