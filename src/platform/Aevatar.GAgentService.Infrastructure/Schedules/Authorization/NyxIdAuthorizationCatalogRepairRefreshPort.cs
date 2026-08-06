using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

internal sealed class NyxIdAuthorizationCatalogRepairRefreshPort
    : INyxIdAuthorizationCatalogRepairRefreshPort
{
    private readonly INyxIdAuthorizationCatalogRepairCommandPort _repairCommandPort;
    private readonly NyxIdAuthorizationCatalogRefreshPipeline _pipeline;
    private readonly TimeProvider _timeProvider;

    public NyxIdAuthorizationCatalogRepairRefreshPort(
        INyxIdAuthorizationCatalogRepairCommandPort repairCommandPort,
        INyxIdAuthorizationCatalogCommandPort commandPort,
        INyxIdApiClientFactory nyxClientFactory,
        INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort observationPreparation,
        INyxIdAuthorizationCatalogRefreshObservationProjectionPort observationProjection,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _repairCommandPort = repairCommandPort ??
                             throw new ArgumentNullException(nameof(repairCommandPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pipeline = new NyxIdAuthorizationCatalogRefreshPipeline(
            commandPort,
            nyxClientFactory,
            observationPreparation,
            observationProjection,
            _timeProvider,
            logger);
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        long minimumSourceStateVersion,
        string repairRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (minimumSourceStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumSourceStateVersion),
                "Minimum source state version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repairRequestId);
        var normalizedOwner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        };
        var normalizedRepairRequestId = repairRequestId.Trim();
        var refreshId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow();
        return _pipeline.RefreshAsync(
            normalizedOwner,
            bearerToken,
            refreshId,
            startedAt,
            requiredServiceIds: null,
            llmTarget: null,
            (refreshIdentity, refreshStartedAt, dispatchCancellation) =>
                _repairCommandPort.BeginRepairRefreshAsync(
                    normalizedOwner,
                    refreshIdentity,
                    refreshStartedAt,
                    minimumSourceStateVersion,
                    normalizedRepairRequestId,
                    dispatchCancellation),
            ct);
    }
}
