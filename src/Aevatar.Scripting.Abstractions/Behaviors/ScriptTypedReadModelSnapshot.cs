namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptTypedReadModelSnapshot(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    string ReadModelTypeUrl,
    IMessage? ReadModel,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
