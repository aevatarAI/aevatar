using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Sisyphus.Application.Tests.Fakes;

internal sealed class FakeWorkflowRunCommandService : IWorkflowRunCommandService
{
    /// <summary>The result that <see cref="ExecuteAsync"/> will return.</summary>
    public WorkflowChatRunExecutionResult? NextResult { get; set; }

    /// <summary>If set, <see cref="ExecuteAsync"/> will throw this instead of returning.</summary>
    public Exception? ThrowOnExecute { get; set; }

    /// <summary>Captured request from the last <see cref="ExecuteAsync"/> call.</summary>
    public WorkflowChatRunRequest? CapturedRequest { get; private set; }

    public Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        CapturedRequest = request;

        if (ThrowOnExecute is not null)
            throw ThrowOnExecute;

        return Task.FromResult(NextResult
            ?? throw new InvalidOperationException("FakeWorkflowRunCommandService.NextResult not set"));
    }
}
