using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowReadModelStartupValidationHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ProjectionDocumentRuntimeOptions _documentRuntimeOptions;
    private readonly ProjectionGraphRuntimeOptions _graphRuntimeOptions;
    private readonly IProjectionDocumentStoreFactory _documentStoreFactory;
    private readonly IProjectionGraphStoreFactory _graphStoreFactory;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        ProjectionDocumentRuntimeOptions documentRuntimeOptions,
        ProjectionGraphRuntimeOptions graphRuntimeOptions,
        IProjectionDocumentStoreFactory documentStoreFactory,
        IProjectionGraphStoreFactory graphStoreFactory,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _documentRuntimeOptions = documentRuntimeOptions;
        _graphRuntimeOptions = graphRuntimeOptions;
        _documentStoreFactory = documentStoreFactory;
        _graphStoreFactory = graphStoreFactory;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return Task.CompletedTask;

        if (_options.ValidateDocumentProviderOnStartup && _documentRuntimeOptions.FailFastOnStartup)
        {
            _documentStoreFactory.Create<WorkflowExecutionReport, string>(
                _serviceProvider,
                _documentRuntimeOptions.ProviderName);
            _logger.LogInformation(
                "Workflow read-model provider startup validation passed. readModelType={ReadModelType} provider={Provider}",
                typeof(WorkflowExecutionReport).FullName,
                _documentRuntimeOptions.ProviderName);
        }

        if (_options.ValidateGraphProviderOnStartup && _graphRuntimeOptions.FailFastOnStartup)
        {
            _graphStoreFactory.Create(
                _serviceProvider,
                _graphRuntimeOptions.ProviderName);
            _logger.LogInformation(
                "Workflow graph provider startup validation passed. graphType={GraphType} provider={Provider}",
                typeof(ProjectionGraphNode).FullName,
                _graphRuntimeOptions.ProviderName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
