// ─────────────────────────────────────────────────────────────
// WorkflowCallModule — 子工作流调用模块
// 仅负责将 workflow_call 步骤转换为内部调用请求事件。
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>子工作流调用模块。处理 type=workflow_call 的步骤。</summary>
public sealed class WorkflowCallModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "workflow_call";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (!payload.Is(StepRequestEvent.Descriptor))
            return;

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "workflow_call")
            return;

        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var parentStepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId ?? string.Empty,
                RunId = parentRunId,
                ExecutionId = request.ExecutionId,
                Success = false,
                Error = "workflow_call missing step_id",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            }, TopologyAudience.Self, ct);
            return;
        }

        var workflowName = request.Parameters.GetValueOrDefault("workflow", "").Trim();
        if (string.IsNullOrEmpty(workflowName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                ExecutionId = request.ExecutionId,
                Success = false,
                Error = "workflow_call missing workflow parameter",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            }, TopologyAudience.Self, ct);
            return;
        }

        var lifecycleRaw = request.Parameters.GetValueOrDefault("lifecycle", string.Empty);
        if (!WorkflowCallLifecycle.IsSupported(lifecycleRaw))
        {
            var invalidLifecycle = lifecycleRaw?.Trim() ?? string.Empty;
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                ExecutionId = request.ExecutionId,
                Success = false,
                Error = $"workflow_call lifecycle must be {WorkflowCallLifecycle.AllowedValuesText}, got '{invalidLifecycle}'",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            }, TopologyAudience.Self, ct);
            return;
        }

        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            parentRunId,
            parentStepId);
        var invocation = new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId),
            ParentRunId = parentRunId,
            ParentStepId = parentStepId,
            ParentExecutionId = request.ExecutionId,
            WorkflowName = workflowName,
            Input = request.Input ?? string.Empty,
            Lifecycle = WorkflowCallLifecycle.Normalize(lifecycleRaw),
            RequestedByActorId = ctx.AgentId,
            RootRunId = runtimeContext.RootRunId,
            RequestedDepth = Math.Max(0, runtimeContext.Depth) + 1,
        };
        invocation.InputFileRefs.Add(request.InputFileRefs.Select(static fileRef => fileRef.Clone()));

        await ctx.PublishAsync(invocation, TopologyAudience.Self, ct);
    }
}
