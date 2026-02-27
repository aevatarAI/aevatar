using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Extracts a workflow YAML from step input, then publishes
/// <see cref="ReconfigureAndExecuteWorkflowEvent"/> so the owning
/// WorkflowGAgent reconfigures itself and restarts execution.
/// </summary>
public sealed class DynamicWorkflowModule : IEventModule
{
    private static readonly Regex YamlFenceRegex = new(
        @"```ya?ml\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "dynamic_workflow";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null && payload.Is(StepRequestEvent.Descriptor);
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (!payload.Is(StepRequestEvent.Descriptor)) return;
        var request = payload.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, "dynamic_workflow", StringComparison.OrdinalIgnoreCase))
            return;

        var originalInput = request.Parameters.GetValueOrDefault("original_input", string.Empty);
        var rawInput = request.Input ?? string.Empty;

        var yaml = ExtractYaml(rawInput);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            ctx.Logger.LogWarning(
                "DynamicWorkflow: run={RunId} step={StepId} — no YAML block found in input",
                request.RunId, request.StepId);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = "No workflow YAML found in input.",
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "DynamicWorkflow: run={RunId} step={StepId} — reconfiguring with extracted YAML ({Len} chars)",
            request.RunId, request.StepId, yaml.Length);

        await ctx.PublishAsync(new ReconfigureAndExecuteWorkflowEvent
        {
            WorkflowYaml = yaml,
            Input = originalInput,
        }, EventDirection.Self, ct);
    }

    internal static string? ExtractYaml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var match = YamlFenceRegex.Match(input);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
