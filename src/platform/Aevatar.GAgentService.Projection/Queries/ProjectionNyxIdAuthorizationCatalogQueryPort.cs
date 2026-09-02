using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ProjectionNyxIdAuthorizationCatalogQueryPort
    : INyxIdAuthorizationCatalogQueryPort
{
    private readonly IProjectionDocumentReader<NyxIdAuthorizationCatalogDocument, string> _reader;

    public ProjectionNyxIdAuthorizationCatalogQueryPort(
        IProjectionDocumentReader<NyxIdAuthorizationCatalogDocument, string> reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
        AuthorizationOwnerIdentity owner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var document = await _reader.GetAsync(NyxIdAuthorizationCatalogActorIds.Build(owner), ct);
        if (document?.Owner == null)
            return null;

        return new NyxIdAuthorizationCatalogSnapshot(
            document.Owner.Clone(),
            document.StateVersion,
            document.ObservedAt,
            document.FreshUntil,
            document.ContractVersion,
            document.PolicyVersion,
            document.EvaluatedAt,
            document.ContentDigest,
            document.Services.Select(static service => service.Clone()).ToArray(),
            document.Invalidated,
            document.InvalidationReason,
            document.LastRefreshFailedAt,
            document.LastRefreshFailureCode,
            document.LifecycleFence,
            document.Activated,
            document.Cleaned,
            document.CleanedAt,
            document.CleanupReason,
            document.GatewayLlmTarget?.Clone());
    }
}
