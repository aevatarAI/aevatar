namespace Aevatar.Foundation.Abstractions.Maintenance;

/// <summary>
/// One module's contribution to startup retired-actor cleanup.
/// Each retired module declares its retired runtime type tokens, the actors that
/// previously persisted those types, and (optionally) how to discover dynamic
/// targets and how to delete its read-model documents.
/// </summary>
public interface IRetiredActorSpec
{
    /// <summary>
    /// Stable, module-scoped identifier (used as the marker stream namespace).
    /// </summary>
    string SpecId { get; }

    /// <summary>
    /// Statically known retired actors (well-known IDs).
    /// </summary>
    IReadOnlyList<RetiredActorTarget> Targets { get; }

    /// <summary>
    /// Discover additional retired actors from data the spec already owns.
    /// Default: no dynamic targets. Used for catalogs whose stream lists generated
    /// child actor ids (e.g. user-agent catalog → skill-runner-* / workflow-agent-*).
    /// </summary>
    IAsyncEnumerable<RetiredActorTarget> DiscoverDynamicTargetsAsync(
        IServiceProvider services,
        CancellationToken ct);

    /// <summary>
    /// Delete read-model documents associated with <paramref name="actorId"/>.
    /// Default: no-op. Specs that own read models override this with their typed
    /// <c>IProjectionDocumentReader</c> / <c>IProjectionWriteDispatcher</c> calls.
    /// </summary>
    Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct);
}
