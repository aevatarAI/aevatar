using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Governance.Hosting.Migration;

public sealed class ServiceGovernanceLegacyMigrationHostedService : IHostedService
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceGovernanceLegacyImporter _legacyImporter;
    private readonly ILogger<ServiceGovernanceLegacyMigrationHostedService> _logger;

    public ServiceGovernanceLegacyMigrationHostedService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceGovernanceLegacyImporter legacyImporter,
        ILogger<ServiceGovernanceLegacyMigrationHostedService> logger)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _legacyImporter = legacyImporter ?? throw new ArgumentNullException(nameof(legacyImporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var services = await _catalogQueryReader.QueryAllAsync(ct: cancellationToken);
        foreach (var service in services)
        {
            var identity = new ServiceIdentity
            {
                TenantId = service.TenantId,
                AppId = service.AppId,
                Namespace = service.Namespace,
                ServiceId = service.ServiceId,
            };

            var imported = await _legacyImporter.ImportIfNeededAsync(identity, cancellationToken);
            if (imported)
                _logger.LogInformation("Imported legacy governance state for {ServiceKey}.", service.ServiceKey);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
