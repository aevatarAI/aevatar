namespace Aevatar.Scripting.Abstractions.Queries;

public sealed record ScriptReadModelSnapshot<TReadModel>(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    string ReadModelTypeUrl,
    TReadModel? ReadModel,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt)
    where TReadModel : class, IMessage<TReadModel>, new();
