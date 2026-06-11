using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Device;

internal sealed class DeviceRegistrationStartupService : IHostedService
{
    // Refactor (iter165/cluster-001): Old pattern: startup owned a Task.Delay retry loop for projection activation. New principle: startup dispatches one bootstrap activation attempt; retry/backoff belongs to actor/runtime scheduling infrastructure, not this hosted service.

    private readonly DeviceRegistrationProjectionBootstrapActivator _projectionActivator;
    private readonly ILogger<DeviceRegistrationStartupService> _logger;

    public DeviceRegistrationStartupService(
        DeviceRegistrationProjectionBootstrapActivator projectionActivator,
        ILogger<DeviceRegistrationStartupService> logger)
    {
        _projectionActivator = projectionActivator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _projectionActivator.ActivateWellKnownRegistryAsync(ct);
            _logger.LogInformation(
                "Device registration projection scope activated for {ActorId}",
                DeviceRegistrationGAgent.WellKnownId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Device registration projection scope activation failed; the host will continue in degraded mode");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
