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
    private readonly IProjectionStoreSelectionPlanner _selectionPlanner;
    private readonly IProjectionStoreSelectionRuntimeOptions _selectionRuntimeOptions;
    private readonly IProjectionStoreStartupValidator _startupValidator;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        IProjectionStoreSelectionPlanner selectionPlanner,
        IProjectionStoreSelectionRuntimeOptions selectionRuntimeOptions,
        IProjectionStoreStartupValidator startupValidator,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _selectionPlanner = selectionPlanner;
        _selectionRuntimeOptions = selectionRuntimeOptions;
        _startupValidator = startupValidator;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return Task.CompletedTask;

        var selectionPlan = _selectionPlanner.Build(
            _selectionRuntimeOptions,
            typeof(WorkflowExecutionReport),
            new ProjectionStoreRequirements());

        if (_options.ValidateDocumentProviderOnStartup)
        {
            var selectedDocumentProvider = _startupValidator.ValidateDocumentProvider<WorkflowExecutionReport, string>(
                _serviceProvider,
                selectionPlan.DocumentSelectionOptions,
                selectionPlan.DocumentRequirements);
            _logger.LogInformation(
                "Workflow read-model provider startup validation passed. readModelType={ReadModelType} provider={Provider}",
                typeof(WorkflowExecutionReport).FullName,
                selectedDocumentProvider.ProviderName);
        }

        if (_options.ValidateGraphProviderOnStartup)
        {
            var selectedGraphProvider = _startupValidator.ValidateGraphProvider(
                _serviceProvider,
                selectionPlan.GraphSelectionOptions,
                selectionPlan.GraphRequirements);
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
