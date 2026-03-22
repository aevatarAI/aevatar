namespace Aevatar.Scripting.Core.Ports;

public interface IScriptCatalogCommandPort
{
    Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct);

    Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        string? scopeId,
        CancellationToken ct) =>
        PromoteCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            expectedBaseRevision,
            revision,
            definitionActorId,
            sourceHash,
            proposalId,
            ct);

    Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct);

    Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        string? scopeId,
        CancellationToken ct) =>
        RollbackCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            targetRevision,
            reason,
            proposalId,
            expectedCurrentRevision,
            ct);
}
