using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionBootstrapHostedService : IHostedService
{
    private readonly IWorkflowDefinitionCatalog _catalog;
    private readonly WorkflowDefinitionFileLoader _loader;
    private readonly IOptions<WorkflowDefinitionFileSourceOptions> _options;
    private readonly IEnumerable<IWorkflowDefinitionSeedSource> _seedSources;
    private readonly ILogger<WorkflowDefinitionBootstrapHostedService> _logger;

    public WorkflowDefinitionBootstrapHostedService(
        IWorkflowDefinitionCatalog catalog,
        WorkflowDefinitionFileLoader loader,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        IEnumerable<IWorkflowDefinitionSeedSource> seedSources,
        ILogger<WorkflowDefinitionBootstrapHostedService> logger)
    {
        _catalog = catalog;
        _loader = loader;
        _options = options;
        _seedSources = seedSources ?? throw new ArgumentNullException(nameof(seedSources));
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _loader.LoadIntoAsync(
            _catalog,
            _options.Value.WorkflowDirectories,
            _logger,
            _seedSources,
            _options.Value.DuplicatePolicy);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
