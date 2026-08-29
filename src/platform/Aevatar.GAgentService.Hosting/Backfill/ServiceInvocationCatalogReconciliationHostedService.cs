using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Hosting.Backfill;

internal sealed class ServiceInvocationCatalogReconciliationHostedService : BackgroundService
{
    private const int MaxReconciliationAttempts = 20;
    private const int SourceReadTake = 10_000;
    private const string PublisherId = "aevatar.service-invocation-catalog-reconciliation";
    private static readonly TimeSpan ReconciliationRetryDelay = TimeSpan.FromSeconds(15);

    private readonly IProjectionDocumentReader<ServiceCatalogReadModel, string> _serviceCatalogReader;
    private readonly IProjectionDocumentReader<ServiceServingSetReadModel, string> _servingSetReader;
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionCatalogReader;
    private readonly IProjectionDocumentReader<ServiceInvocationCatalogReadModel, string> _invocationCatalogReader;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ServiceInvocationCatalogReconciliationHostedService> _logger;

    public ServiceInvocationCatalogReconciliationHostedService(
        IProjectionDocumentReader<ServiceCatalogReadModel, string> serviceCatalogReader,
        IProjectionDocumentReader<ServiceServingSetReadModel, string> servingSetReader,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionCatalogReader,
        IProjectionDocumentReader<ServiceInvocationCatalogReadModel, string> invocationCatalogReader,
        IActorDispatchPort dispatchPort,
        ILogger<ServiceInvocationCatalogReconciliationHostedService> logger)
    {
        _serviceCatalogReader = serviceCatalogReader;
        _servingSetReader = servingSetReader;
        _revisionCatalogReader = revisionCatalogReader;
        _invocationCatalogReader = invocationCatalogReader;
        _dispatchPort = dispatchPort;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        for (var attempt = 1; attempt <= MaxReconciliationAttempts; attempt++)
        {
            if (await RunReconciliationOnceAsync(stoppingToken))
                return;

            if (attempt == MaxReconciliationAttempts)
            {
                _logger.LogWarning(
                    "Service invocation catalog reconciliation did not converge after {AttemptCount} attempts; a future pod restart will retry.",
                    MaxReconciliationAttempts);
                return;
            }

            try
            {
                await Task.Delay(ReconciliationRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<bool> RunReconciliationOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunReconciliationCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Service invocation catalog reconciliation attempt failed; startup reconciliation will retry.");
            return false;
        }
    }

    private async Task<bool> RunReconciliationCoreAsync(CancellationToken cancellationToken)
    {
        var serviceCatalogs = await QueryAllAsync(_serviceCatalogReader, cancellationToken);
        var servingSets = await QueryAllAsync(_servingSetReader, cancellationToken);
        var revisionCatalogs = await QueryAllAsync(_revisionCatalogReader, cancellationToken);
        var invocationCatalogs = await QueryAllAsync(_invocationCatalogReader, cancellationToken);

        var servingByService = servingSets.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var revisionsByService = revisionCatalogs.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var invocationByService = invocationCatalogs.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var stale = 0;
        var refreshed = 0;
        var failed = 0;

        foreach (var service in serviceCatalogs)
        {
            if (!TryBuildIdentity(service, out var identity) ||
                !servingByService.TryGetValue(service.Id, out var servingSet) ||
                !revisionsByService.TryGetValue(service.Id, out var revisionCatalog))
            {
                continue;
            }

            invocationByService.TryGetValue(service.Id, out var invocationCatalog);
            if (!RequiresRefresh(service, servingSet, revisionCatalog, invocationCatalog))
                continue;

            stale++;
            var dispatched = 0;
            foreach (var actorId in new[]
                     {
                         ServiceActorIds.Definition(identity),
                         ServiceActorIds.RevisionCatalog(identity),
                         ServiceActorIds.ServingSet(identity),
                     })
            {
                try
                {
                    await DispatchRefreshAsync(actorId, identity, cancellationToken);
                    dispatched++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(
                        ex,
                        "Service invocation catalog refresh dispatch failed. serviceKey={ServiceKey} sourceActorId={SourceActorId}",
                        service.Id,
                        actorId);
                }
            }

            if (dispatched == 3)
                refreshed++;
        }

        _logger.LogInformation(
            "Service invocation catalog reconciliation attempt completed. scanned={ScannedCount} stale={StaleCount} refreshed={RefreshedCount} failedDispatches={FailedDispatchCount}",
            serviceCatalogs.Count,
            stale,
            refreshed,
            failed);
        return stale == 0;
    }

    private async Task DispatchRefreshAsync(
        string actorId,
        ServiceIdentity identity,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(now),
            Payload = Any.Pack(new RefreshServiceInvocationCatalogObservationCommand
            {
                Identity = identity.Clone(),
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation(),
        };
        var admission = await _dispatchPort.DispatchAsync(actorId, envelope, cancellationToken);
        if (!admission.Accepted)
            throw new InvalidOperationException($"Service invocation catalog refresh was not admitted for actor '{actorId}'.");
    }

    private static bool RequiresRefresh(
        ServiceCatalogReadModel service,
        ServiceServingSetReadModel servingSet,
        ServiceRevisionCatalogReadModel revisionCatalog,
        ServiceInvocationCatalogReadModel? invocationCatalog)
    {
        if (invocationCatalog == null ||
            invocationCatalog.SourceCatalogVersion != service.StateVersion ||
            invocationCatalog.SourceServingVersion != servingSet.StateVersion ||
            invocationCatalog.SourceRevisionVersion != revisionCatalog.StateVersion)
        {
            return true;
        }

        var endpointIds = service.Endpoints
            .Select(static endpoint => endpoint.EndpointId?.Trim() ?? string.Empty)
            .Where(static endpointId => endpointId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (invocationCatalog.Entries.Count != endpointIds.Length)
            return true;

        foreach (var endpointId in endpointIds)
        {
            var entries = invocationCatalog.Entries
                .Where(entry => string.Equals(entry.EndpointId, endpointId, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length != 1)
                return true;

            var target = SelectTarget(servingSet.Targets, endpointId);
            var entry = entries[0];
            if (target == null)
            {
                if (entry.ReadinessStatus != ServiceInvokeReadinessStatus.Unavailable ||
                    entry.UnavailableReason != ServiceInvokeUnavailableReason.ServingTargetMissing ||
                    !string.IsNullOrWhiteSpace(entry.SelectedRevisionId) ||
                    !string.IsNullOrWhiteSpace(entry.SelectedDeploymentId) ||
                    !string.IsNullOrWhiteSpace(entry.SelectedActorId))
                {
                    return true;
                }

                continue;
            }

            if (!string.Equals(entry.SelectedRevisionId, target.RevisionId, StringComparison.Ordinal) ||
                !string.Equals(entry.SelectedDeploymentId, target.DeploymentId, StringComparison.Ordinal) ||
                !string.Equals(entry.SelectedActorId, target.PrimaryActorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ServiceServingTargetReadModel? SelectTarget(
        IEnumerable<ServiceServingTargetReadModel> targets,
        string endpointId) =>
        targets
            .Where(target =>
                target.AllocationWeight > 0 &&
                string.Equals(target.ServingState, ServiceServingState.Active.ToString(), StringComparison.OrdinalIgnoreCase) &&
                (target.EnabledEndpointIds.Count == 0 ||
                 target.EnabledEndpointIds.Any(id => string.Equals(id, endpointId, StringComparison.Ordinal))))
            .OrderByDescending(static target => target.AllocationWeight)
            .ThenBy(static target => target.RevisionId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool TryBuildIdentity(ServiceCatalogReadModel service, out ServiceIdentity identity)
    {
        identity = new ServiceIdentity
        {
            TenantId = service.TenantId?.Trim() ?? string.Empty,
            AppId = service.AppId?.Trim() ?? string.Empty,
            Namespace = service.Namespace?.Trim() ?? string.Empty,
            ServiceId = service.ServiceId?.Trim() ?? string.Empty,
        };
        return identity.TenantId.Length > 0 &&
               identity.AppId.Length > 0 &&
               identity.Namespace.Length > 0 &&
               identity.ServiceId.Length > 0 &&
               string.Equals(service.Id, ServiceKeys.Build(identity), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<TReadModel>> QueryAllAsync<TReadModel>(
        IProjectionDocumentReader<TReadModel, string> reader,
        CancellationToken cancellationToken)
        where TReadModel : class, IProjectionReadModel
    {
        var documents = new List<TReadModel>();
        string? cursor = null;
        do
        {
            var result = await reader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Take = SourceReadTake,
                    Cursor = cursor,
                },
                cancellationToken);
            documents.AddRange(result.Items);
            cursor = result.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return documents;
    }
}
