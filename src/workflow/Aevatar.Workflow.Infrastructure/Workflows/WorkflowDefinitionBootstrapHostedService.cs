using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionBootstrapHostedService : IHostedService
{
    private readonly IWorkflowDefinitionCatalog _catalog;
    private readonly WorkflowDefinitionFileLoader _loader;
    private readonly IOptions<WorkflowDefinitionFileSourceOptions> _options;
    private readonly ILogger<WorkflowDefinitionBootstrapHostedService> _logger;

    public WorkflowDefinitionBootstrapHostedService(
        IWorkflowDefinitionCatalog catalog,
        WorkflowDefinitionFileLoader loader,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<WorkflowDefinitionBootstrapHostedService> logger)
    {
        _catalog = catalog;
        _loader = loader;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loader.LoadInto(
            _catalog,
            _options.Value.WorkflowDirectories,
            _logger,
            _options.Value.DuplicatePolicy);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
