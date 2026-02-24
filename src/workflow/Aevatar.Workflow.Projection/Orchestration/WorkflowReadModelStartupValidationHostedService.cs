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
            new ProjectionReadModelRequirements());

        if (_options.ValidateReadModelProviderOnStartup)
        {
            var selectedReadModelProvider = _startupValidator.ValidateReadModelProvider<WorkflowExecutionReport, string>(
                _serviceProvider,
                selectionPlan.ReadModelSelectionOptions,
                selectionPlan.ReadModelRequirements);
            _logger.LogInformation(
                "Workflow read-model provider startup validation passed. readModelType={ReadModelType} provider={Provider}",
                typeof(WorkflowExecutionReport).FullName,
                selectedReadModelProvider.ProviderName);
        }

        if (_options.ValidateRelationProviderOnStartup)
        {
            var selectedRelationProvider = _startupValidator.ValidateRelationProvider(
                _serviceProvider,
                selectionPlan.RelationSelectionOptions,
                selectionPlan.RelationRequirements);
            _logger.LogInformation(
                "Workflow relation provider startup validation passed. relationType={RelationType} provider={Provider}",
                typeof(ProjectionRelationNode).FullName,
                selectedRelationProvider.ProviderName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
