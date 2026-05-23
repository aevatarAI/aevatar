using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Device;

internal sealed class DeviceRegistrationStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

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
        var delay = InitialDelay;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _projectionActivator.ActivateWellKnownRegistryAsync(ct);
                _logger.LogInformation(
                    "Device registration projection scope activated for {ActorId} (attempt {Attempt})",
                    DeviceRegistrationGAgent.WellKnownId,
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
                    "Failed to activate device registration projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt,
                    MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);

                delay *= 2;
            }
        }

        _logger.LogError(
            "Device registration projection scope activation failed after {MaxRetries} attempts",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
