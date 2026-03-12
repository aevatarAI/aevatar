using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Validates workflow YAML from step input.
/// Emits success with canonical fenced YAML when valid, or failure with validation details.
/// </summary>
public sealed class WorkflowYamlValidateModule : IEventModule
{
    public string Name => "workflow_yaml_validate";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
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
            }, EventDirection.Self, ct);
            return;
        }

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(yaml, ctx);
        if (errors.Count > 0)
        {
            var validationDetails = string.Join("; ", errors);
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Output = $"""
Previous workflow draft:
```yaml
{yaml}
```

Validation error:
{validationDetails}

Return a corrected full workflow YAML only in a single ```yaml fenced block.
""",
                Success = false,
                Error = $"Invalid workflow YAML: {validationDetails}",
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
