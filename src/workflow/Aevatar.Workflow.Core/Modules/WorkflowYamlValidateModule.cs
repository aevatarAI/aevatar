using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Validates workflow YAML from step input.
/// Emits success with canonical fenced YAML when valid, or failure with validation details.
/// </summary>
public sealed class WorkflowYamlValidateModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "workflow_yaml_validate";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(StepRequestEvent.Descriptor))
            return;

        var request = payload.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, Name, StringComparison.OrdinalIgnoreCase))
            return;

        var rawInput = request.Input ?? string.Empty;
        var yaml = DynamicWorkflowModule.ExtractYaml(rawInput);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = "No workflow YAML found in input.",
            }, TopologyAudience.Self, ct);
            return;
        }

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(yaml, ctx);
        if (errors.Count > 0)
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"Invalid workflow YAML: {string.Join("; ", errors)}",
            }, TopologyAudience.Self, ct);
            return;
        }

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = $"```yaml\n{yaml}\n```",
        }, TopologyAudience.Self, ct);
    }
}
