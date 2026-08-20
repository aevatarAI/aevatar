// ─────────────────────────────────────────────────────────────
// AssignModule — 变量赋值模块
// 从点号路径或字面值赋值到变量
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>变量赋值模块。处理 type=assign 的步骤。</summary>
public sealed class AssignModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "assign";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "assign") return;

        // 参数: target = 目标变量名, value = 值或路径
        var target = request.Parameters.GetValueOrDefault("target", "");
        var value = request.Parameters.GetValueOrDefault("value", "");

        // 如果 value 以 $ 开头，表示从 input（上一步输出）中取值
        var resolvedValue = value.StartsWith('$') ? request.Input ?? string.Empty : value;

        // Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
        //   Old pattern: Information log included raw value/prompt/input preview
        //   New principle: only stable id + length + status + redaction marker
        ctx.Logger.LogInformation(
            "Assign: run={RunId} step={StepId} target={Target} status=assigned value_len={ValueLen} value_redacted=true",
            request.RunId,
            request.StepId,
            target,
            resolvedValue.Length);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            Success = true,
            Output = resolvedValue,
            OutputProvenance = value.StartsWith('$')
                ? WorkflowStepOutputProvenance.ForwardedInput
                : WorkflowStepOutputProvenance.Produced,
        };
        if (!string.IsNullOrWhiteSpace(target))
        {
            completed.AssignedVariable = target;
            completed.AssignedValue = resolvedValue;
            completed.AssignedValueProvenance =
                WorkflowStepAssignedValueProvenance.ReferencesOutput;
        }

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }
}
