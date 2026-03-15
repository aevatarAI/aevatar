using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionPortServices
    : IServiceCatalogProjectionPort,
      IServiceDeploymentCatalogProjectionPort,
      IServiceRevisionCatalogProjectionPort,
      IServiceServingSetProjectionPort,
      IServiceRolloutProjectionPort,
      IServiceTrafficViewProjectionPort
{
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>> _catalogActivationService;
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>> _deploymentActivationService;
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>> _revisionActivationService;
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>> _servingActivationService;
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>> _rolloutActivationService;
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>> _trafficActivationService;

    public ServiceProjectionPortServices(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>> catalogActivationService,
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>> deploymentActivationService,
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>> revisionActivationService,
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>> servingActivationService,
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>> rolloutActivationService,
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>> trafficActivationService)
    {
        _catalogActivationService = catalogActivationService ?? throw new ArgumentNullException(nameof(catalogActivationService));
        _deploymentActivationService = deploymentActivationService ?? throw new ArgumentNullException(nameof(deploymentActivationService));
        _revisionActivationService = revisionActivationService ?? throw new ArgumentNullException(nameof(revisionActivationService));
        _servingActivationService = servingActivationService ?? throw new ArgumentNullException(nameof(servingActivationService));
        _rolloutActivationService = rolloutActivationService ?? throw new ArgumentNullException(nameof(rolloutActivationService));
        _trafficActivationService = trafficActivationService ?? throw new ArgumentNullException(nameof(trafficActivationService));
    }

    Task IServiceCatalogProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_catalogActivationService, ServiceProjectionNames.Catalog, actorId, ct);

    Task IServiceDeploymentCatalogProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_deploymentActivationService, ServiceProjectionNames.Deployments, actorId, ct);

    Task IServiceRevisionCatalogProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_revisionActivationService, ServiceProjectionNames.Revisions, actorId, ct);

    Task IServiceServingSetProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_servingActivationService, ServiceProjectionNames.Serving, actorId, ct);

    Task IServiceRolloutProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_rolloutActivationService, ServiceProjectionNames.Rollouts, actorId, ct);

    Task IServiceTrafficViewProjectionPort.EnsureProjectionAsync(string actorId, CancellationToken ct) =>
        EnsureProjectionAsync(_trafficActivationService, ServiceProjectionNames.Traffic, actorId, ct);

    private static async Task EnsureProjectionAsync<TContext>(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>> activationService,
        string projectionName,
        string actorId,
        CancellationToken ct)
        where TContext : class, IProjectionContext
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await activationService.EnsureAsync(actorId, projectionName, string.Empty, actorId, ct);
    }
}
