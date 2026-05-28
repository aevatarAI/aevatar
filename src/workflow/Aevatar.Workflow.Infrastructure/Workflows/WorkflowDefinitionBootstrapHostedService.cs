using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionBootstrapHostedService : IHostedService
{
    private readonly IWorkflowDefinitionCatalog _registry;
    private readonly WorkflowDefinitionFileLoader _loader;
    private readonly FileBackedWorkflowCatalogPort _definitionMaterializer;
    private readonly IOptions<WorkflowDefinitionFileSourceOptions> _options;
    private readonly ILogger<WorkflowDefinitionBootstrapHostedService> _logger;

    public WorkflowDefinitionBootstrapHostedService(
        IWorkflowDefinitionCatalog registry,
        WorkflowDefinitionFileLoader loader,
        FileBackedWorkflowCatalogPort definitionMaterializer,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<WorkflowDefinitionBootstrapHostedService> logger)
    {
        _registry = registry;
        _loader = loader;
        _definitionMaterializer = definitionMaterializer;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loader.LoadInto(
            _registry,
            _options.Value.WorkflowDirectories,
            _logger,
            _options.Value.DuplicatePolicy);
        var definitions = _registry.GetNames()
            .Select(name => _registry.GetDefinition(name))
            .Where(definition => definition != null)
            .Select(definition => definition!)
            .ToList();
        await _definitionMaterializer.MaterializeAsync(definitions, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
