using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogCommandPort : INyxIdAuthorizationCatalogCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public NyxIdAuthorizationCatalogCommandPort(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task BeginRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset startedAtUtc,
        long expectedLifecycleFence,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new BeginNyxIdAuthorizationCatalogRefreshCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            StartedAt = Timestamp.FromDateTimeOffset(startedAtUtc),
            ExpectedLifecycleFence = expectedLifecycleFence,
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
            CoverageKind = ToCoverageKindState(observation.Coverage),
        };
        command.Services.Add(observation.Services.Select(static service => service.Clone()));
        if (observation.GatewayLLMTarget != null)
            command.GatewayLlmTarget = observation.GatewayLLMTarget.Clone();
        command.CoveredUserServiceIds.Add((observation.CoveredUserServiceIds ?? [])
            .Select(static serviceId => serviceId?.Trim() ?? string.Empty)
            .Where(static serviceId => serviceId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static serviceId => serviceId, StringComparer.Ordinal));
        return DispatchAsync(observation.Owner, command, ct);
    }

    public Task RecordRefreshFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset failedAtUtc,
        string failureCode,
        NyxIdAuthorizationCatalogRefreshStatus status = NyxIdAuthorizationCatalogRefreshStatus.Failed,
        CancellationToken ct = default) =>
        DispatchAsync(owner, new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            FailedAt = Timestamp.FromDateTimeOffset(failedAtUtc),
            FailureCode = failureCode ?? string.Empty,
            OutcomeStatus = ToRefreshFailureOutcomeStatusState(status),
        }, ct);

    public Task InvalidateAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default) =>
        InvalidateCoreAsync(
            owner,
            string.Empty,
            invalidatedAtUtc,
            reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Unspecified,
            ct);

    public Task InvalidateRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus outcomeStatus,
        CancellationToken ct = default) =>
        InvalidateCoreAsync(
            owner,
            refreshId,
            invalidatedAtUtc,
            reason,
            ToOutcomeStatusState(outcomeStatus),
            ct);

    private Task InvalidateCoreAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState outcomeStatus,
        CancellationToken ct) =>
        DispatchAsync(owner, new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            RefreshId = refreshId ?? string.Empty,
            InvalidatedAt = Timestamp.FromDateTimeOffset(invalidatedAtUtc),
            Reason = reason ?? string.Empty,
            OutcomeStatus = outcomeStatus,
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
        CancellationToken ct) =>
        await NyxIdAuthorizationCatalogCommandDispatch.DispatchAsync(
            _runtime,
            _dispatchPort,
            owner,
            command,
            ct);

    private static NyxIdAuthorizationCatalogRefreshOutcomeStatusState ToOutcomeStatusState(
        NyxIdAuthorizationCatalogRefreshOutcomeStatus status) => status switch
    {
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Catalog refresh invalidation requires an access-denied or unstable outcome."),
    };

    private static NyxIdAuthorizationCatalogRefreshOutcomeStatusState ToRefreshFailureOutcomeStatusState(
        NyxIdAuthorizationCatalogRefreshStatus status) => status switch
    {
        NyxIdAuthorizationCatalogRefreshStatus.Failed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
        NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Refresh failure recording only supports failed or catalog-unstable outcomes."),
    };

    private static NyxIdAuthorizationCatalogObservationCoverageKind ToCoverageKindState(
        NyxIdAuthorizationCatalogObservationCoverage coverage) => coverage switch
    {
        NyxIdAuthorizationCatalogObservationCoverage.FullOwner =>
            NyxIdAuthorizationCatalogObservationCoverageKind.FullOwner,
        NyxIdAuthorizationCatalogObservationCoverage.RequiredServiceSubset =>
            NyxIdAuthorizationCatalogObservationCoverageKind.RequiredServiceSubset,
        _ => throw new ArgumentOutOfRangeException(nameof(coverage), coverage, null),
    };
}

internal static class NyxIdAuthorizationCatalogCommandDispatch
{
    private const string PublisherId = "gagent-service.nyxid-authorization-catalog";

    public static async Task DispatchAsync(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        AuthorizationOwnerIdentity owner,
        Google.Protobuf.IMessage command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var actor = await runtime.GetAsync(actorId) ??
                    await runtime.CreateAsync<NyxIdAuthorizationCatalogGAgent>(actorId, ct);
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actor.Id),
            Propagation = new EnvelopePropagation { CorrelationId = commandId },
        };
        await dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
