using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchNyxIdCatalogSnapshotCommandPort(
    IStudioActorBootstrap bootstrap,
    StudioProjectionActorCommandDispatch commandDispatch)
    : INyxIdCatalogSnapshotCommandPort
{
    private const string PublisherId = "aevatar.studio.nyxid-catalog-lifecycle";

    public Task ObserveAsync(NyxIdCatalogObservation observation, CancellationToken ct = default)
    {
        var command = new ObserveNyxIdCatalogSnapshotCommand
        {
            Owner = MapOwner(observation.Owner),
            ObservedAt = Timestamp.FromDateTimeOffset(observation.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(observation.FreshUntilUtc),
            ExternalRevision = observation.ExternalRevision,
            ContentDigest = observation.ContentDigest,
        };
        command.Services.Add(observation.Services.Select(MapService));
        return DispatchAsync(observation.Owner, command, ct);
    }

    public Task RecordRefreshFailureAsync(
        NyxIdCatalogOwnerIdentity owner,
        DateTimeOffset failedAtUtc,
        string failureCode,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new RecordNyxIdCatalogSnapshotRefreshFailureCommand
        {
            Owner = MapOwner(owner),
            FailedAt = Timestamp.FromDateTimeOffset(failedAtUtc),
            FailureCode = failureCode,
        }, ct);

    public Task InvalidateAsync(
        NyxIdCatalogOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new InvalidateNyxIdCatalogSnapshotCommand
        {
            Owner = MapOwner(owner),
            InvalidatedAt = Timestamp.FromDateTimeOffset(invalidatedAtUtc),
            Reason = reason,
        }, ct);

    private async Task DispatchAsync(NyxIdCatalogOwnerIdentity owner, Google.Protobuf.IMessage command, CancellationToken ct)
    {
        var actor = await bootstrap.EnsureAsync<NyxIdCatalogSnapshotGAgent>(BuildActorId(owner), ct);
        await commandDispatch.DispatchAsync(actor, command, PublisherId, ct: ct);
    }

    private static string BuildActorId(NyxIdCatalogOwnerIdentity owner)
    {
        var identity = $"{owner.Authority.Trim()}\n{(int)owner.OwnerKind}\n{owner.OwnerSubject.Trim()}";
        return $"studio-nyxid-catalog-snapshot:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    private static NyxIdCatalogSnapshotOwner MapOwner(NyxIdCatalogOwnerIdentity owner) => new()
    {
        Authority = owner.Authority,
        OwnerKind = (NyxIdCatalogSnapshotOwnerKind)(int)owner.OwnerKind,
        OwnerSubject = owner.OwnerSubject,
    };

    private static NyxIdCatalogSnapshotService MapService(NyxIdServiceGrant service)
    {
        var mapped = new NyxIdCatalogSnapshotService
        {
            UserServiceId = service.UserServiceId,
            ServiceSlug = service.ServiceSlug,
            DisplayName = service.DisplayName,
            NodeGrantsNotRequired = service.NodeGrantsNotRequired,
            Reachable = true,
        };
        mapped.Nodes.Add(service.NodeGrants.Select(static node => new NyxIdCatalogSnapshotNode
        {
            NodeId = node.NodeId,
            DisplayName = node.DisplayName,
            Primary = node.Primary,
        }));
        return mapped;
    }
}
