using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowRunCommandService
{
    private readonly IWorkflowRunContextFactory _runContextFactory;
    private readonly IWorkflowRunExecutionEngine _runExecutionEngine;

    public WorkflowChatRunApplicationService(
        IWorkflowRunContextFactory runContextFactory,
        IWorkflowRunExecutionEngine runExecutionEngine,
        ILogger<WorkflowChatRunApplicationService>? logger = null)
    {
        _runContextFactory = runContextFactory;
        _runExecutionEngine = runExecutionEngine;
        _ = logger ?? NullLogger<WorkflowChatRunApplicationService>.Instance;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(WorkflowChatRunRequest request, Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        return await ExecuteWithoutFallbackAsync(request, emitAsync, onStartedAsync, ct);
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
