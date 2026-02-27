using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowRunCommandService
{
    private readonly IWorkflowRunContextFactory _runContextFactory;
    private readonly IWorkflowRunExecutionEngine _runExecutionEngine;
    private readonly WorkflowDirectFallbackPolicy _fallbackPolicy;
    private readonly ILogger<WorkflowChatRunApplicationService> _logger;

    public WorkflowChatRunApplicationService(
        IWorkflowRunContextFactory runContextFactory,
        IWorkflowRunExecutionEngine runExecutionEngine,
        WorkflowDirectFallbackPolicy fallbackPolicy,
        ILogger<WorkflowChatRunApplicationService>? logger = null)
    {
        _runContextFactory = runContextFactory;
        _runExecutionEngine = runExecutionEngine;
        _fallbackPolicy = fallbackPolicy;
        _logger = logger ?? NullLogger<WorkflowChatRunApplicationService>.Instance;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(WorkflowChatRunRequest request, Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        try
        {
            return await ExecuteWithoutFallbackAsync(request, emitAsync, onStartedAsync, ct);
        }
        catch (Exception ex) when (_fallbackPolicy.ShouldFallback(request, ex))
        {
            var fallbackRequest = _fallbackPolicy.ToFallbackRequest(request);
            _logger.LogWarning(ex, "Workflow run failed and falls back to direct. workflow={WorkflowName}, actorId={ActorId}, hasInlineYaml={HasInlineYaml}", request.WorkflowName ?? "<null>", request.ActorId ?? "<null>", !string.IsNullOrWhiteSpace(request.WorkflowYaml));
            return await ExecuteWithoutFallbackAsync(fallbackRequest, emitAsync, onStartedAsync, ct);
        }
    }

    private async Task<WorkflowChatRunExecutionResult> ExecuteWithoutFallbackAsync(WorkflowChatRunRequest request, Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync, CancellationToken ct)
    {
        var runContextCreateResult = await _runContextFactory.CreateAsync(request, ct);
        if (runContextCreateResult.Error != WorkflowChatRunStartError.None ||
            runContextCreateResult.Context == null)
        {
            return new WorkflowChatRunExecutionResult(runContextCreateResult.Error, null, null);
        }

        return await _runExecutionEngine.ExecuteAsync(runContextCreateResult.Context, request, emitAsync, onStartedAsync, ct);
    }
}
