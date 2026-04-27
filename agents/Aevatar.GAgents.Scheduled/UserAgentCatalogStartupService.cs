using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentCatalogStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly string LegacyProjectionScopeActorId = ProjectionScopeActorId.Build(
        new ProjectionRuntimeScopeKey(
            UserAgentCatalogGAgent.WellKnownId,
            UserAgentCatalogStorageContracts.LegacyDurableProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization));

    private readonly UserAgentCatalogProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly ILogger<UserAgentCatalogStartupService> _logger;

    public UserAgentCatalogStartupService(
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider,
        ILogger<UserAgentCatalogStartupService> logger)
    {
        _projectionPort = projectionPort;
        _actorRuntime = actorRuntime;
        _streamProvider = streamProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var delay = InitialDelay;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await CleanupLegacyProjectionScopeAsync(ct);
                await _projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);
                _logger.LogInformation(
                    "User agent catalog projection scope activated for {ActorId} (attempt {Attempt})",
                    UserAgentCatalogGAgent.WellKnownId,
                    attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to activate user agent catalog projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt,
                    MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);

                delay *= 2;
            }
        }

        _logger.LogError(
            "User agent catalog projection scope activation failed after {MaxRetries} attempts",
            MaxRetries);
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
