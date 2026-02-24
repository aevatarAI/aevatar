using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowReadModelStartupValidationHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return Task.CompletedTask;

        if (_options.ValidateDocumentProviderOnStartup)
        {
            _ = _serviceProvider.GetRequiredService<IDocumentProjectionStore<WorkflowExecutionReport, string>>();
            _logger.LogInformation(
                "Workflow read-model document startup validation passed. readModelType={ReadModelType}",
                typeof(WorkflowExecutionReport).FullName);
        }

        if (_options.ValidateGraphProviderOnStartup)
        {
            _ = _serviceProvider.GetRequiredService<IProjectionGraphStore>();
            _logger.LogInformation(
                "Workflow read-model graph startup validation passed. graphType={GraphType}",
                typeof(ProjectionGraphNode).FullName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
