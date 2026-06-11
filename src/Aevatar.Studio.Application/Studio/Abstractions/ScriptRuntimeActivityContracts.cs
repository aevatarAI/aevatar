namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IScriptRuntimeActivityQueryPort
{
    Task<ScriptRuntimeActivitySnapshot?> GetAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScriptRuntimeActivitySnapshot>> ListAsync(
        int take = 200,
        CancellationToken ct = default);
}

public sealed record ScriptRuntimeActivitySnapshot(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    string Input,
    string Output,
    string Status,
    string LastCommandId,
    IReadOnlyList<string> Notes,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
