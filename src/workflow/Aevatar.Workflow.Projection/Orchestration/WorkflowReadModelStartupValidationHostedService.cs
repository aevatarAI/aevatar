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
    private readonly IProjectionDocumentRuntimeOptions _documentRuntimeOptions;
    private readonly IProjectionGraphRuntimeOptions _graphRuntimeOptions;
    private readonly IProjectionDocumentStartupValidator _documentStartupValidator;
    private readonly IProjectionGraphStartupValidator _graphStartupValidator;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        IProjectionDocumentRuntimeOptions documentRuntimeOptions,
        IProjectionGraphRuntimeOptions graphRuntimeOptions,
        IProjectionDocumentStartupValidator documentStartupValidator,
        IProjectionGraphStartupValidator graphStartupValidator,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _documentRuntimeOptions = documentRuntimeOptions;
        _graphRuntimeOptions = graphRuntimeOptions;
        _documentStartupValidator = documentStartupValidator;
        _graphStartupValidator = graphStartupValidator;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return Task.CompletedTask;

        if (_options.ValidateDocumentProviderOnStartup && _documentRuntimeOptions.FailFastOnStartup)
        {
            var selectedDocumentProvider = _documentStartupValidator.ValidateProvider<WorkflowExecutionReport, string>(
                _serviceProvider,
                new ProjectionDocumentSelectionOptions
                {
                    RequestedProviderName = _documentRuntimeOptions.ProviderName,
                });
            _logger.LogInformation(
                "Workflow read-model provider startup validation passed. readModelType={ReadModelType} provider={Provider}",
                typeof(WorkflowExecutionReport).FullName,
                selectedDocumentProvider.ProviderName);
        }

        if (_options.ValidateGraphProviderOnStartup && _graphRuntimeOptions.FailFastOnStartup)
        {
            var selectedGraphProvider = _graphStartupValidator.ValidateProvider(
                _serviceProvider,
                new ProjectionGraphSelectionOptions
                {
                    RequestedProviderName = _graphRuntimeOptions.ProviderName,
                });
            _logger.LogInformation(
                "Workflow graph provider startup validation passed. graphType={GraphType} provider={Provider}",
                typeof(ProjectionGraphNode).FullName,
                selectedGraphProvider.ProviderName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
