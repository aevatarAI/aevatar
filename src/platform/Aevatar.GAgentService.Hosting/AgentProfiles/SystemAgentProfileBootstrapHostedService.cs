using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal sealed class SystemAgentProfileBootstrapHostedService : BackgroundService
{
    private readonly ISystemAgentProfileProvisioningService _provisioningService;
    private readonly ISystemAgentProfileBootstrapSignal _signal;

    public SystemAgentProfileBootstrapHostedService(
        ISystemAgentProfileProvisioningService provisioningService,
        ISystemAgentProfileBootstrapSignal signal)
    {
        _provisioningService = provisioningService ??
            throw new ArgumentNullException(nameof(provisioningService));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _provisioningService.ReconcileAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);
                await _provisioningService.ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
