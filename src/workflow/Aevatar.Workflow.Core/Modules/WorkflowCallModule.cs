// ─────────────────────────────────────────────────────────────
// WorkflowCallModule — 子工作流调用模块
// 递归调用另一个 workflow（通过 StartWorkflowEvent）
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>子工作流调用模块。处理 type=workflow_call 的步骤。</summary>
public sealed class WorkflowCallModule : IEventModule
{
    private readonly Dictionary<string, PendingWorkflowCall> _pendingByChildRunId = new(StringComparer.Ordinal);

    public string Name => "workflow_call";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "workflow_call") return;

            var workflowName = request.Parameters.GetValueOrDefault("workflow", "");
            if (string.IsNullOrEmpty(workflowName))
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = request.RunId,
                    Success = false,
                    Error = "workflow_call 缺少 workflow 参数",
                }, EventDirection.Self, ct);
                return;
            }

            var parentRunId = string.IsNullOrWhiteSpace(request.RunId) ? "default" : request.RunId;
            var childRunId = BuildChildRunId(parentRunId, request.StepId);
            _pendingByChildRunId[childRunId] = new PendingWorkflowCall(
                request.StepId,
                parentRunId,
                workflowName);

            ctx.Logger.LogInformation(
                "WorkflowCall: parentStep={StepId}, parentRun={ParentRun} → childWorkflow={Workflow}, childRun={ChildRun}",
                request.StepId,
                parentRunId,
                workflowName,
                childRunId);

            var start = new StartWorkflowEvent
            {
                WorkflowName = workflowName,
                Input = request.Input,
                RunId = childRunId,
            };
            start.Parameters["workflow_call.parent_step_id"] = request.StepId;
            start.Parameters["workflow_call.parent_run_id"] = parentRunId;
            start.Parameters["workflow_call.requested_workflow"] = workflowName;

            await ctx.PublishAsync(start, EventDirection.Self, ct);
            return;
        }

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<WorkflowCompletedEvent>();
            var childRunId = completed.RunId;
            if (string.IsNullOrWhiteSpace(childRunId) ||
                !_pendingByChildRunId.Remove(childRunId, out var pending))
            {
                return;
            }

            var parentCompleted = new StepCompletedEvent
            {
                StepId = pending.ParentStepId,
                RunId = pending.ParentRunId,
                Success = completed.Success,
                Output = completed.Output,
                Error = completed.Error,
            };
            parentCompleted.Metadata["workflow_call.child_run_id"] = childRunId;
            parentCompleted.Metadata["workflow_call.workflow_name"] = pending.WorkflowName;

            await ctx.PublishAsync(parentCompleted, EventDirection.Self, ct);
        }
    }

    private static string BuildChildRunId(string parentRunId, string parentStepId) =>
        $"{parentRunId}:workflow_call:{parentStepId}:{Guid.NewGuid():N}";

    private sealed record PendingWorkflowCall(string ParentStepId, string ParentRunId, string WorkflowName);
}
