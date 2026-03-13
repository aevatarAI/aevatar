namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptFactContext(
    string ActorId,
    string DefinitionActorId,
    string ScriptId,
    string Revision,
    string RunId,
    string CommandId,
    string CorrelationId,
    long EventSequence,
    long StateVersion,
    string EventType,
    long OccurredAtUnixTimeMs);
