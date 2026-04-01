namespace Aevatar.AI.ToolProviders.Scripting.Ports;

/// <summary>
/// Adapter port for querying script catalog and definition read models.
/// Named distinctly from domain-level query ports in Aevatar.Scripting.Core to avoid collision.
/// Implementation reads from projection-materialized stores.
/// </summary>
public interface IScriptToolCatalogQueryAdapter
{
    /// <summary>List scripts in the catalog, optionally filtered by keyword.</summary>
    Task<IReadOnlyList<ScriptCatalogEntry>> ListAsync(
        string? filter, int maxResults, CancellationToken ct = default);

    /// <summary>Get a specific script's catalog entry.</summary>
    Task<ScriptCatalogEntry?> GetAsync(string scriptId, CancellationToken ct = default);

    /// <summary>Get detailed definition info for a script revision.</summary>
    Task<ScriptDefinitionInfo?> GetDefinitionAsync(
        string scriptId, string? revision, CancellationToken ct = default);

    /// <summary>Get source code for a specific script revision.</summary>
    Task<ScriptSourceSnapshot?> GetSourceAsync(
        string scriptId, string revision, CancellationToken ct = default);
}

/// <summary>Script catalog entry (read model).</summary>
public sealed record ScriptCatalogEntry
{
    public required string ScriptId { get; init; }
    public string? ActiveRevision { get; init; }
    public IReadOnlyList<string> RevisionHistory { get; init; } = [];
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>Detailed script definition information.</summary>
public sealed record ScriptDefinitionInfo
{
    public required string ScriptId { get; init; }
    public required string Revision { get; init; }
    public string? SourceHash { get; init; }
    public string? StateTypeUrl { get; init; }
    public string? ReadModelTypeUrl { get; init; }
    public IReadOnlyList<string> CommandTypeUrls { get; init; } = [];
    public IReadOnlyList<string> DomainEventTypeUrls { get; init; } = [];
    public IReadOnlyList<string> SignalTypeUrls { get; init; } = [];
    public DateTimeOffset? CompiledAt { get; init; }
}

/// <summary>Source code snapshot for a script revision.</summary>
public sealed record ScriptSourceSnapshot
{
    public required string ScriptId { get; init; }
    public required string Revision { get; init; }

    /// <summary>Source files keyed by filename.</summary>
    public required IReadOnlyDictionary<string, string> SourceFiles { get; init; }

    /// <summary>Proto files keyed by filename, if any.</summary>
    public IReadOnlyDictionary<string, string>? ProtoFiles { get; init; }
}
