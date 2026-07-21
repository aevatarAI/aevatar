using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort
    : INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<
        NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<
        NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> _releaseService;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>
        _logger;

    public NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
            activationService,
        IProjectionScopeReleaseService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
            releaseService,
        ILogger<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>? logger = null)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _logger = logger ??
                  NullLogger<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>.Instance;
    }

    public async Task<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string refreshId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(refreshId))
            return null;

        var normalizedActorId = actorId.Trim();
        var normalizedRefreshId = refreshId.Trim();
        var lease = new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
            new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
            {
                RootActorId = normalizedActorId,
                ProjectionKind = ServiceProjectionKinds.NyxIdAuthorizationCatalogRefreshObservation,
                SessionId = normalizedRefreshId,
            });
        try
        {
            lease = await _activationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = ServiceProjectionKinds.NyxIdAuthorizationCatalogRefreshObservation,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedRefreshId,
                },
                ct).ConfigureAwait(false);

            return new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
                normalizedActorId,
                normalizedRefreshId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseAfterFailureAsync(lease).ConfigureAwait(false);

            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Failed to prepare a NyxID catalog refresh observation scope for actor {ActorId} and refresh {RefreshId}.",
                normalizedActorId,
                normalizedRefreshId);
            await ReleaseAfterFailureAsync(lease).ConfigureAwait(false);

            return null;
        }
    }

    private async Task ReleaseAfterFailureAsync(
        NyxIdAuthorizationCatalogRefreshObservationRuntimeLease lease)
    {
        try
        {
            await _releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Failed to roll back a NyxID catalog refresh observation scope after preparation failure.");
        }
    }

    public Task ReleaseAsync(
        NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        var lease = new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
            new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
            {
                RootActorId = preparation.ActorId,
                ProjectionKind = ServiceProjectionKinds.NyxIdAuthorizationCatalogRefreshObservation,
                SessionId = preparation.RefreshId,
            });
        return _releaseService.ReleaseIfIdleAsync(lease, ct);
    }
}
