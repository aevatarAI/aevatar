namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptQueryContext<TReadModel>(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    TReadModel? CurrentReadModel,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt)
    where TReadModel : class, IMessage<TReadModel>, new();
