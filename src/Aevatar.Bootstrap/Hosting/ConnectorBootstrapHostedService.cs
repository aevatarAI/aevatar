using Aevatar.Bootstrap.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

public sealed class ConnectorBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConnectorBootstrapHostedService> _logger;

    public ConnectorBootstrapHostedService(
        IServiceProvider serviceProvider,
        ILogger<ConnectorBootstrapHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetService<IConnectorRegistry>();
        if (registry == null)
        {
            _logger.LogDebug("Skip connector bootstrap because IConnectorRegistry is not registered.");
            return;
        }

        var connectorBuilders = scope.ServiceProvider.GetServices<IConnectorBuilder>();
        await ConnectorRegistration.RegisterConnectorsAsync(registry, connectorBuilders, _logger, ct: cancellationToken);

        var names = registry.ListNames();
        if (names.Count > 0)
            _logger.LogInformation("Connectors loaded: {Count} [{Names}]", names.Count, string.Join(", ", names));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
