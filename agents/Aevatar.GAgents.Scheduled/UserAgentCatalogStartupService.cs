using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogStartupService : IHostedService
{
    // Refactor (iter165/cluster-001): Old pattern: startup owned a Task.Delay retry loop for projection activation. New principle: startup dispatches one bootstrap activation attempt; retry/backoff belongs to actor/runtime scheduling infrastructure, not this hosted service.
    private static readonly string LegacyProjectionScopeActorId = ProjectionScopeActorId.Build(
        new ProjectionRuntimeScopeKey(
            UserAgentCatalogGAgent.WellKnownId,
            UserAgentCatalogStorageContracts.LegacyDurableProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization));

    private readonly UserAgentCatalogProjectionBootstrapActivator _projectionActivator;
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly ILogger<UserAgentCatalogStartupService> _logger;

    public UserAgentCatalogStartupService(
        UserAgentCatalogProjectionBootstrapActivator projectionActivator,
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider,
        ILogger<UserAgentCatalogStartupService> logger)
    {
        _projectionActivator = projectionActivator;
        _actorRuntime = actorRuntime;
        _streamProvider = streamProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await CleanupLegacyProjectionScopeAsync(ct);
            await _projectionActivator.ActivateWellKnownCatalogAsync(ct);
            _logger.LogInformation(
                "User agent catalog projection scope activated for {ActorId}",
                UserAgentCatalogGAgent.WellKnownId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "User agent catalog projection scope activation failed; the host will continue in degraded mode");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CleanupLegacyProjectionScopeAsync(CancellationToken ct)
    {
        await _streamProvider
            .GetStream(UserAgentCatalogGAgent.WellKnownId)
            .RemoveRelayAsync(LegacyProjectionScopeActorId, ct);

        if (!await _actorRuntime.ExistsAsync(LegacyProjectionScopeActorId))
            return;

        await _actorRuntime.DestroyAsync(LegacyProjectionScopeActorId, ct);
        _logger.LogInformation(
            "Removed legacy user agent catalog projection scope {LegacyActorId}",
            LegacyProjectionScopeActorId);
    }
}
