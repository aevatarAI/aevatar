using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Extracts a workflow YAML from step input, then publishes
/// <see cref="ReplaceWorkflowDefinitionAndExecuteEvent"/> so the owning
/// <see cref="WorkflowRunGAgent"/> can replace the in-flight run binding and continue from the new definition.
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

        var validationErrors = ValidateWorkflowYaml(yaml, ctx);
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
            "DynamicWorkflow: run={RunId} step={StepId} — reconfiguring with extracted YAML ({Len} chars)",
            request.RunId, request.StepId, yaml.Length);

        await ctx.PublishAsync(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
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

    internal static List<string> ValidateWorkflowYaml(string yaml, IEventHandlerContext ctx)
    {
        WorkflowDefinition parsed;
        try
        {
            parsed = new WorkflowParser().Parse(yaml);
        }
        catch (Exception ex)
        {
            return [$"YAML parse failed: {ex.Message}"];
        }

        var knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            ctx.Services.GetServices<IWorkflowModulePack>()
                .SelectMany(pack => pack.Modules)
                .SelectMany(module => module.Names));
        knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);

        var moduleFactory = ctx.Services.GetService<IEventModuleFactory>();
        if (moduleFactory != null)
            ExpandKnownStepTypesFromFactory(parsed.Steps, knownStepTypes, moduleFactory);

        return WorkflowValidator.Validate(
            parsed,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = knownStepTypes,
            },
            availableWorkflowNames: null);
    }

    private static void ExpandKnownStepTypesFromFactory(
        IEnumerable<StepDefinition> steps,
        ISet<string> knownStepTypes,
        IEventModuleFactory moduleFactory)
    {
        foreach (var stepType in EnumerateReferencedStepTypes(steps))
        {
            var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
            if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                continue;

            if (moduleFactory.TryCreate(canonical, out _))
                knownStepTypes.Add(canonical);
        }
    }

    private static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Type;

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                    yield return childType;
            }
        }
    }
}
