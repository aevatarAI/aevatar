using Aevatar.CQRS.Projection.Abstractions;
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
    private readonly IWorkflowReadModelSelectionPlanner _selectionPlanner;
    private readonly IProjectionReadModelProviderRegistry _providerRegistry;
    private readonly IProjectionReadModelProviderSelector _providerSelector;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        IWorkflowReadModelSelectionPlanner selectionPlanner,
        IProjectionReadModelProviderRegistry providerRegistry,
        IProjectionReadModelProviderSelector providerSelector,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _selectionPlanner = selectionPlanner;
        _providerRegistry = providerRegistry;
        _providerSelector = providerSelector;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || !_options.ValidateReadModelProviderOnStartup)
            return Task.CompletedTask;

        var selectionPlan = _selectionPlanner.Build(_options);

        var registrations = _providerRegistry.GetRegistrations<WorkflowExecutionReport, string>(_serviceProvider);
        var selected = _providerSelector.Select(registrations, selectionPlan.SelectionOptions, selectionPlan.Requirements);
        _logger.LogInformation(
            "Workflow read-model provider startup validation passed. readModelType={ReadModelType} provider={Provider}",
            typeof(WorkflowExecutionReport).FullName,
            selected.ProviderName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
