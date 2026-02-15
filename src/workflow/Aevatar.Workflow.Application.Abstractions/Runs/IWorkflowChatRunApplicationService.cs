using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Orchestration;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowChatRunApplicationService
{
    Task<WorkflowChatRunPreparationResult> PrepareAsync(
        WorkflowChatRunRequest request,
        IAGUIEventSink sink,
        CancellationToken ct = default);

    Task StreamEventsUntilTerminalAsync(
        IAGUIEventSink sink,
        string runId,
        Func<AGUIEvent, Task> emitAsync,
        CancellationToken ct = default);

    Task<WorkflowProjectionFinalizeResult> FinalizeProjectionAsync(
        WorkflowChatRunExecution execution,
        CancellationToken ct = default);

    Task WriteArtifactsBestEffortAsync(
        WorkflowProjectionFinalizeResult finalizeResult,
        CancellationToken ct = default);

    Task RollbackAndJoinAsync(
        WorkflowChatRunExecution execution,
        bool projectionsFinalized,
        CancellationToken ct = default);

    bool IsTerminalEventForRun(AGUIEvent evt, string runId);
}
