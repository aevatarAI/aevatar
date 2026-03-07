using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Extracts a workflow YAML from step input and asks the owning
/// <see cref="WorkflowRunGAgent"/> to spawn a derived child run from it.
/// </summary>
public sealed class DynamicWorkflowModule : IWorkflowPrimitiveHandler
{
    private static readonly Regex YamlFenceRegex = new(
        @"```ya?ml\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "dynamic_workflow";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
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

        var validationErrors = ValidateWorkflowYaml(yaml, ctx.KnownStepTypes);
        if (validationErrors.Count > 0)
        {
            var errorMessage = string.Join("; ", validationErrors);
            ctx.Logger.LogWarning(
                "DynamicWorkflow: run={RunId} step={StepId} — yaml validation failed: {Error}",
                request.RunId,
                request.StepId,
                errorMessage);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"Invalid workflow YAML: {errorMessage}",
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "DynamicWorkflow: run={RunId} step={StepId} — spawning derived child workflow ({Len} chars)",
            request.RunId, request.StepId, yaml.Length);

        await ctx.PublishAsync(new DynamicWorkflowInvokeRequestedEvent
        {
            InvocationId = $"{request.RunId}:dynamic:{request.StepId}:{Guid.NewGuid():N}",
            ParentRunId = request.RunId,
            ParentStepId = request.StepId,
            WorkflowName = DeriveWorkflowName(yaml, request.StepId),
            WorkflowYaml = yaml,
            Input = originalInput,
        }, EventDirection.Self, ct);
    }

    internal static string? ExtractYaml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string? lastYaml = null;
        var matches = YamlFenceRegex.Matches(input);
        foreach (Match match in matches)
        {
            if (match.Success)
                lastYaml = match.Groups[1].Value.Trim();
        }

        return string.IsNullOrWhiteSpace(lastYaml) ? null : lastYaml;
    }

    internal static List<string> ValidateWorkflowYaml(string yaml, IReadOnlySet<string> knownStepTypes) =>
        WorkflowYamlValidationSupport.ValidateWorkflowYaml(yaml, knownStepTypes);

    internal static string DeriveWorkflowName(string yaml, string stepId)
    {
        try
        {
            var parsed = new WorkflowParser().Parse(yaml);
            if (!string.IsNullOrWhiteSpace(parsed.Name))
                return parsed.Name.Trim();
        }
        catch
        {
        }

        return $"derived_{stepId.Trim()}";
    }
}
