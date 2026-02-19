using Aevatar.Bootstrap.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

public sealed class ConnectorBootstrapHostedService : IHostedService
{
    private readonly IConnectorRegistry _registry;
    private readonly IEnumerable<IConnectorBuilder> _connectorBuilders;
    private readonly ILogger<ConnectorBootstrapHostedService> _logger;

    public ConnectorBootstrapHostedService(
        IConnectorRegistry registry,
        IEnumerable<IConnectorBuilder> connectorBuilders,
        ILogger<ConnectorBootstrapHostedService> logger)
    {
        _registry = registry;
        _connectorBuilders = connectorBuilders;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ConnectorRegistration.RegisterConnectors(_registry, _connectorBuilders, _logger);

        var names = _registry.ListNames();
        if (names.Count > 0)
            _logger.LogInformation("Connectors loaded: {Count} [{Names}]", names.Count, string.Join(", ", names));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
