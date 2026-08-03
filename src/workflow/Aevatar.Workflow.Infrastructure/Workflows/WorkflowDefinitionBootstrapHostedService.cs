using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionBootstrapHostedService : IHostedLifecycleService
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
        if (_options.Value.BindCommitMaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.Value.BindCommitMaxAttempts,
                "Workflow definition bind commit max attempts must be positive.");
        if (_options.Value.BindCommitRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.Value.BindCommitRetryDelay,
                "Workflow definition bind commit retry delay cannot be negative.");
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loader.LoadInto(
            _registry,
            _options.Value.WorkflowDirectories,
            _logger,
            _options.Value.DuplicatePolicy);
        return Task.CompletedTask;
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definitions = _registry.GetNames()
            .Select(name => _registry.GetDefinition(name))
            .Where(definition => definition != null)
            .Select(definition => definition!)
            .ToList();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _definitionMaterializer.MaterializeAsync(definitions, cancellationToken);
                return;
            }
            catch (WorkflowDefinitionMaterializationException ex) when (
                IsTransient(ex) && attempt < _options.Value.BindCommitMaxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Startup workflow definition bind attempt {Attempt}/{MaxAttempts} did not reach committed observation; retrying after {RetryDelay}.",
                    attempt,
                    _options.Value.BindCommitMaxAttempts,
                    _options.Value.BindCommitRetryDelay);
                await Task.Delay(_options.Value.BindCommitRetryDelay, cancellationToken);
            }
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsTransient(WorkflowDefinitionMaterializationException exception) =>
        exception.Code is
            WorkflowDefinitionMaterializationException.ObservationUnavailableCode or
            WorkflowDefinitionMaterializationException.BindNotCommittedCode;
}
