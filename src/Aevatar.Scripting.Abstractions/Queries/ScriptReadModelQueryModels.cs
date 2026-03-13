using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Queries;

public sealed record ScriptReadModelSnapshot(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    string ReadModelTypeUrl,
    Any? ReadModelPayload,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
