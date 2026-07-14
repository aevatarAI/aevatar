using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionNyxIdCatalogSnapshotQueryPort : INyxIdCatalogSnapshotQueryPort
{
    private readonly IProjectionDocumentReader<NyxIdCatalogSnapshotCurrentStateDocument, string> _reader;

    public ProjectionNyxIdCatalogSnapshotQueryPort(
        IProjectionDocumentReader<NyxIdCatalogSnapshotCurrentStateDocument, string> reader) => _reader = reader;

    public async Task<NyxIdCatalogSnapshot?> GetAsync(
        NyxIdCatalogOwnerIdentity owner,
        CancellationToken ct = default)
    {
        var result = await _reader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                Equal("authority", owner.Authority),
                Equal("owner_kind", (int)owner.OwnerKind),
                Equal("owner_subject", owner.OwnerSubject),
            ],
            Take = 2,
        }, ct);
        var document = result.Items.SingleOrDefault();
        if (document == null || document.Invalidated)
            return null;

        return new NyxIdCatalogSnapshot(
            owner.Clone(),
            document.StateVersion,
            document.ObservedAt.ToDateTimeOffset(),
            document.FreshUntil.ToDateTimeOffset(),
            document.ExternalRevision,
            document.ContentDigest,
            document.Services.Select(MapService).ToArray(),
            document.Services.Where(static service => !service.Reachable)
                .Select(static service => service.UserServiceId)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static ProjectionDocumentFilter Equal(string field, string value) => new()
    {
        FieldPath = field,
        Operator = ProjectionDocumentFilterOperator.Eq,
        Value = ProjectionDocumentValue.FromString(value),
    };

    private static ProjectionDocumentFilter Equal(string field, int value) => new()
    {
        FieldPath = field,
        Operator = ProjectionDocumentFilterOperator.Eq,
        Value = ProjectionDocumentValue.FromInt64(value),
    };

    private static NyxIdServiceGrant MapService(NyxIdCatalogSnapshotServiceReadModel service)
    {
        var grant = new NyxIdServiceGrant
        {
            UserServiceId = service.UserServiceId,
            DisplayName = service.DisplayName,
            NodeGrantsNotRequired = service.NodeGrantsNotRequired,
            ServiceSlug = service.ServiceSlug,
        };
        grant.NodeGrants.Add(service.Nodes.Select(static node => new NyxIdNodeGrant
        {
            NodeId = node.NodeId,
            DisplayName = node.DisplayName,
            Primary = node.Primary,
        }));
        return grant;
    }
}
