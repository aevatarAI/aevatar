namespace Aevatar.Scripting.Core.Ports;

public interface IScriptCatalogQueryPort
{
    Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct);

    Task<IReadOnlyList<ScriptCatalogEntrySnapshot>> ListCatalogEntriesAsync(
        string? catalogActorId,
        int take,
        CancellationToken ct);
}
