using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

internal sealed class NyxIdAuthorizationCatalogRepairCommandPort
    : INyxIdAuthorizationCatalogRepairCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public NyxIdAuthorizationCatalogRepairCommandPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task BeginRepairRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset startedAtUtc,
        long minimumSourceStateVersion,
        string repairRequestId,
        CancellationToken ct = default) =>
        NyxIdAuthorizationCatalogCommandDispatch.DispatchAsync(
            _runtime,
            _dispatchPort,
            owner,
            new BeginNyxIdAuthorizationCatalogRepairRefreshCommand
            {
                Owner = owner.Clone(),
                RefreshId = refreshId ?? string.Empty,
                StartedAt = Timestamp.FromDateTimeOffset(startedAtUtc),
                MinimumSourceStateVersion = minimumSourceStateVersion,
                RepairRequestId = repairRequestId ?? string.Empty,
            },
            ct);
}
