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
    private CancellationTokenSource? _retryCts;
    private Task? _retryTask;

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
        try
        {
            await _definitionMaterializer.MaterializeAsync(definitions, cancellationToken);
        }
        catch (WorkflowDefinitionMaterializationException ex)
            when (ex.Code == WorkflowDefinitionMaterializationException.BindNotCommittedCode &&
                  !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Startup workflow definition bind was not observed before timeout; host startup will continue and retry materialization in the background. workflow_name={WorkflowName} actor_id={ActorId} expected_execution_mode={ExpectedExecutionMode}",
                ex.WorkflowName,
                ex.ActorId,
                ex.ExpectedExecutionMode);
            StartBackgroundRetry(definitions);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_retryCts == null || _retryTask == null)
            return;

        await _retryCts.CancelAsync();
        try
        {
            await _retryTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _retryCts.Dispose();
            _retryCts = null;
            _retryTask = null;
        }
    }

    private void StartBackgroundRetry(IReadOnlyList<WorkflowDefinitionRegistration> definitions)
    {
        var retryDelay = _options.Value.BindCommitRetryDelay;
        if (retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WorkflowDefinitionFileSourceOptions.BindCommitRetryDelay),
                retryDelay,
                "Workflow definition bind commit retry delay must be positive.");
        }

        _retryCts = new CancellationTokenSource();
        _retryTask = RetryMaterializationAsync(definitions, retryDelay, _retryCts.Token);
    }

    private async Task RetryMaterializationAsync(
        IReadOnlyList<WorkflowDefinitionRegistration> definitions,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(retryDelay, ct);
                await _definitionMaterializer.MaterializeAsync(definitions, ct);
                _logger.LogInformation("Startup workflow definition materialization completed in background retry.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (WorkflowDefinitionMaterializationException ex)
                when (ex.Code == WorkflowDefinitionMaterializationException.BindNotCommittedCode)
            {
                _logger.LogWarning(
                    ex,
                    "Startup workflow definition bind still has not been observed; materialization will retry. workflow_name={WorkflowName} actor_id={ActorId} expected_execution_mode={ExpectedExecutionMode}",
                    ex.WorkflowName,
                    ex.ActorId,
                    ex.ExpectedExecutionMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup workflow definition materialization retry failed; materialization will retry.");
            }
        }
    }
}
