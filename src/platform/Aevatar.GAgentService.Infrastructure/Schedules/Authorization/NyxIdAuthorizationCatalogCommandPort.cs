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

    public Task ActivateAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset activatedAtUtc,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new ActivateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            ActivatedAt = Timestamp.FromDateTimeOffset(activatedAtUtc),
        }, ct);

    public Task BeginRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset startedAtUtc,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new BeginNyxIdAuthorizationCatalogRefreshCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            StartedAt = Timestamp.FromDateTimeOffset(startedAtUtc),
        }, ct);

    public Task ObserveAsync(NyxIdAuthorizationCatalogObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var command = new ObserveNyxIdAuthorizationCatalogCommand
        {
            Owner = observation.Owner.Clone(),
            RefreshId = observation.RefreshId,
            ObservedAt = Timestamp.FromDateTimeOffset(observation.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(observation.FreshUntilUtc),
            ContractVersion = observation.ContractVersion,
            PolicyVersion = observation.PolicyVersion,
            EvaluatedAt = Timestamp.FromDateTimeOffset(observation.EvaluatedAtUtc),
            ContentDigest = observation.ContentDigest,
        };
        command.Services.Add(observation.Services.Select(static service => service.Clone()));
        return DispatchAsync(observation.Owner, command, ct);
    }

    public Task RecordRefreshFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset failedAtUtc,
        string failureCode,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            FailedAt = Timestamp.FromDateTimeOffset(failedAtUtc),
            FailureCode = failureCode ?? string.Empty,
        }, ct);

    public Task InvalidateAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        InvalidateCoreAsync(owner, string.Empty, invalidatedAtUtc, reason, ct);

    public Task InvalidateRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        InvalidateCoreAsync(owner, refreshId, invalidatedAtUtc, reason, ct);

    private Task InvalidateCoreAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct) =>
        DispatchAsync(owner, new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            InvalidatedAt = Timestamp.FromDateTimeOffset(invalidatedAtUtc),
            Reason = reason ?? string.Empty,
        }, ct);

    public Task CleanupAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset cleanedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(cleanedAtUtc),
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
