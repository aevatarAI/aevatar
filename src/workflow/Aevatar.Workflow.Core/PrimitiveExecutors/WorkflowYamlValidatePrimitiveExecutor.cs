using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>
/// Validates workflow YAML from step input.
/// Emits success with canonical fenced YAML when valid, or failure with validation details.
/// </summary>
public sealed class WorkflowYamlValidatePrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "workflow_yaml_validate";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (!string.Equals(request.StepType, Name, StringComparison.OrdinalIgnoreCase))
            return;

        var rawInput = request.Input ?? string.Empty;
        var yaml = DynamicWorkflowPrimitiveExecutor.ExtractYaml(rawInput);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = "No workflow YAML found in input.",
            }, EventDirection.Self, ct);
            return;
        }

        var errors = DynamicWorkflowPrimitiveExecutor.ValidateWorkflowYaml(yaml, ctx.KnownStepTypes);
        if (errors.Count > 0)
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"Invalid workflow YAML: {string.Join("; ", errors)}",
            }, EventDirection.Self, ct);
            return;
        }

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = $"```yaml\n{yaml}\n```",
        }, EventDirection.Self, ct);
    }
}
