using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowChatRunApplicationService : IWorkflowRunCommandService
{
    private readonly IWorkflowRunContextFactory _runContextFactory;
    private readonly IWorkflowRunExecutionEngine _runExecutionEngine;

    public WorkflowChatRunApplicationService(
        IWorkflowRunContextFactory runContextFactory,
        IWorkflowRunExecutionEngine runExecutionEngine)
    {
        _runContextFactory = runContextFactory;
        _runExecutionEngine = runExecutionEngine;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var runContextCreateResult = await _runContextFactory.CreateAsync(request, ct);
        if (runContextCreateResult.Error != WorkflowChatRunStartError.None ||
            runContextCreateResult.Context == null)
        {
            return new WorkflowChatRunExecutionResult(runContextCreateResult.Error, null, null);
        }

        return await _runExecutionEngine.ExecuteAsync(
            runContextCreateResult.Context,
            request,
            emitAsync,
            onStartedAsync,
            ct);
    }
}
