namespace Aevatar.AI.ToolProviders.Scripting.Ports;

/// <summary>
/// Adapter port for script catalog write operations (promote, rollback).
/// Named distinctly from the domain-level port in Aevatar.Scripting.Core.Ports to avoid collision.
/// Implementation bridges to ScriptCatalogGAgent via the actor dispatch pipeline,
/// resolving catalogActorId and definitionActorId internally.
/// </summary>
public interface IScriptToolCatalogCommandAdapter
{
    /// <summary>Promote a compiled revision to active in the catalog.</summary>
    Task<ScriptCatalogCommandResult> PromoteAsync(
        string scriptId, string revision, CancellationToken ct = default);

    /// <summary>Rollback the active revision. If targetRevision is null, rolls back to previous.</summary>
    Task<ScriptCatalogCommandResult> RollbackAsync(
        string scriptId, string? targetRevision = null, string? expectedCurrentRevision = null,
        CancellationToken ct = default);
}

/// <summary>Result of a catalog command.</summary>
public sealed record ScriptCatalogCommandResult
{
    public required bool Success { get; init; }

    /// <summary>The active revision after the operation.</summary>
    public string? ActiveRevision { get; init; }

    /// <summary>The previous revision (before promote, or rolled-back-to revision).</summary>
    public string? PreviousRevision { get; init; }

    /// <summary>Error message if the operation failed.</summary>
    public string? Error { get; init; }
}
