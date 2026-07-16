using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogCommandPort : INyxIdAuthorizationCatalogCommandPort
{
    private const string PublisherId = "gagent-service.nyxid-authorization-catalog";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public NyxIdAuthorizationCatalogCommandPort(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task ObserveAsync(NyxIdAuthorizationCatalogObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var command = new ObserveNyxIdAuthorizationCatalogCommand
        {
            Owner = observation.Owner.Clone(),
            ObservedAt = Timestamp.FromDateTimeOffset(observation.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(observation.FreshUntilUtc),
            ExternalRevision = observation.ExternalRevision,
            ContentDigest = observation.ContentDigest,
        };
        command.Services.Add(observation.Services.Select(static service => service.Clone()));
        return DispatchAsync(observation.Owner, command, ct);
    }

    public Task RecordRefreshFailureAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset failedAtUtc,
        string failureCode,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            FailedAt = Timestamp.FromDateTimeOffset(failedAtUtc),
            FailureCode = failureCode ?? string.Empty,
        }, ct);

    public Task InvalidateAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(invalidatedAtUtc),
            Reason = reason ?? string.Empty,
        }, ct);

    private async Task DispatchAsync(
        AuthorizationOwnerIdentity owner,
        Google.Protobuf.IMessage command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var actor = await _runtime.GetAsync(actorId) ??
                    await _runtime.CreateAsync<NyxIdAuthorizationCatalogGAgent>(actorId, ct);
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actor.Id),
            Propagation = new EnvelopePropagation { CorrelationId = commandId },
        };
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
