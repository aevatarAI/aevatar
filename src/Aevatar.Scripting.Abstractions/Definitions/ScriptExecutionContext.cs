namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptExecutionContext(
    string ActorId,
    string ScriptId,
    string Revision);
