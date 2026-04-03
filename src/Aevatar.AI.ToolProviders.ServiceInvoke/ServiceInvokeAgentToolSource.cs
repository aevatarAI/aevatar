using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ServiceInvoke.Schema;
using Aevatar.AI.ToolProviders.ServiceInvoke.Tools;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.ServiceInvoke;

public sealed class ServiceInvokeAgentToolSource : IAgentToolSource
{
    private readonly ServiceInvokeOptions _options;
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly IServiceInvocationPort _invocationPort;
    private readonly EndpointSchemaProvider? _schemaProvider;
    private readonly ILogger _logger;

    public ServiceInvokeAgentToolSource(
        ServiceInvokeOptions options,
        IServiceCatalogQueryReader catalogReader,
        IServiceInvocationPort invocationPort,
        IServiceRevisionArtifactStore? artifactStore = null,
        ILogger<ServiceInvokeAgentToolSource>? logger = null)
    {
        _options = options;
        _catalogReader = catalogReader;
        _invocationPort = invocationPort;
        _schemaProvider = artifactStore != null ? new EndpointSchemaProvider(artifactStore) : null;
        _logger = logger ?? NullLogger<ServiceInvokeAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (!_options.EnableDynamicScopeResolution &&
            (string.IsNullOrWhiteSpace(_options.TenantId) ||
             string.IsNullOrWhiteSpace(_options.AppId) ||
             string.IsNullOrWhiteSpace(_options.Namespace)))
        {
            _logger.LogDebug("ServiceInvoke scope not fully configured (tenant/app/namespace required), skipping");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>
        {
            new ListServicesTool(_catalogReader, _options, _schemaProvider),
        };

        if (_options.EnableInvoke)
            tools.Add(new InvokeServiceTool(_invocationPort, _catalogReader, _options, _schemaProvider));

        _logger.LogInformation("ServiceInvoke tools registered ({Count} tools, schema: {SchemaAvailable})",
            tools.Count, _schemaProvider != null);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
