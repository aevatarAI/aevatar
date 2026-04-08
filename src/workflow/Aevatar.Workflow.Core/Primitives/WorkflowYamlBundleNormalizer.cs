using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.Workflow.Core.Primitives;

internal sealed class WorkflowYamlBundleNormalizer
{
    private const string SyntheticWorkflowPrefix = "__inline__";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly WorkflowParser _parser = new();

    private readonly record struct ParsedInlineWorkflow(string Key, WorkflowDefinition Workflow);

    public WorkflowYamlBundleNormalizationResult Normalize(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null)
    {
        if (string.IsNullOrWhiteSpace(workflowYaml))
        {
            return new WorkflowYamlBundleNormalizationResult(
                workflowYaml ?? string.Empty,
                NormalizePassthroughInlineWorkflows(inlineWorkflowYamls));
        }

        var rootWorkflow = _parser.Parse(workflowYaml);
        var parsedInlineWorkflows = ParseInlineWorkflows(inlineWorkflowYamls);
        var reservedWorkflowNames = BuildReservedWorkflowNames(rootWorkflow, parsedInlineWorkflows);
        var generatedWorkflows = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);

        var normalizedRoot = NormalizeWorkflow(rootWorkflow, reservedWorkflowNames, generatedWorkflows);
        var normalizedInline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inlineWorkflow in parsedInlineWorkflows.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedInlineWorkflow = NormalizeWorkflow(inlineWorkflow.Workflow, reservedWorkflowNames, generatedWorkflows);
            var normalizedKey = WorkflowRunIdNormalizer.NormalizeWorkflowName(inlineWorkflow.Key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
                continue;

            normalizedInline[normalizedKey] =
                SerializeWorkflow(normalizedInlineWorkflow);
        }

        foreach (var syntheticWorkflow in generatedWorkflows.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            normalizedInline[syntheticWorkflow.Key] = SerializeWorkflow(syntheticWorkflow.Value);

        return new WorkflowYamlBundleNormalizationResult(
            SerializeWorkflow(normalizedRoot),
            normalizedInline);
    }

    private static IReadOnlyDictionary<string, string> NormalizePassthroughInlineWorkflows(
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        if (inlineWorkflowYamls == null || inlineWorkflowYamls.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in inlineWorkflowYamls)
        {
            var normalizedName = WorkflowRunIdNormalizer.NormalizeWorkflowName(key);
            if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(value))
                continue;

            normalized[normalizedName] = value;
        }

        return normalized;
    }

    private static IReadOnlyList<ParsedInlineWorkflow> ParseInlineWorkflows(
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        if (inlineWorkflowYamls == null || inlineWorkflowYamls.Count == 0)
            return [];

        var parser = new WorkflowParser();
        var parsed = new List<ParsedInlineWorkflow>(inlineWorkflowYamls.Count);
        foreach (var (key, yaml) in inlineWorkflowYamls)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                continue;

            parsed.Add(new ParsedInlineWorkflow(key, parser.Parse(yaml)));
        }

        return parsed;
    }

    private static HashSet<string> BuildReservedWorkflowNames(
        WorkflowDefinition rootWorkflow,
        IReadOnlyList<ParsedInlineWorkflow> inlineWorkflows)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WorkflowRunIdNormalizer.NormalizeWorkflowName(rootWorkflow.Name),
        };

        foreach (var inlineWorkflow in inlineWorkflows)
        {
            var normalizedKey = WorkflowRunIdNormalizer.NormalizeWorkflowName(inlineWorkflow.Key);
            if (!string.IsNullOrWhiteSpace(normalizedKey))
                reserved.Add(normalizedKey);

            var normalizedName = WorkflowRunIdNormalizer.NormalizeWorkflowName(inlineWorkflow.Workflow.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
                reserved.Add(normalizedName);
        }

        return reserved;
    }

    private WorkflowDefinition NormalizeWorkflow(
        WorkflowDefinition workflow,
        ISet<string> reservedWorkflowNames,
        IDictionary<string, WorkflowDefinition> generatedWorkflows)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        return new WorkflowDefinition
        {
            Name = workflow.Name,
            Description = workflow.Description,
            Roles = workflow.Roles.Select(CloneRole).ToList(),
            Steps = NormalizeSteps(workflow, reservedWorkflowNames, generatedWorkflows),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = workflow.Configuration.ClosedWorldMode,
            },
        };
    }

    private List<StepDefinition> NormalizeSteps(
        WorkflowDefinition workflow,
        ISet<string> reservedWorkflowNames,
        IDictionary<string, WorkflowDefinition> generatedWorkflows)
    {
        var normalized = new List<StepDefinition>(workflow.Steps.Count);
        foreach (var step in workflow.Steps)
            normalized.Add(NormalizeStep(step, workflow, reservedWorkflowNames, generatedWorkflows));
        return normalized;
    }

    private StepDefinition NormalizeStep(
        StepDefinition step,
        WorkflowDefinition workflow,
        ISet<string> reservedWorkflowNames,
        IDictionary<string, WorkflowDefinition> generatedWorkflows)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workflow);

        var hasChildren = step.Children is { Count: > 0 };
        if (!string.Equals(step.Type, "while", StringComparison.OrdinalIgnoreCase) || !hasChildren)
            return CloneStep(step);

        ValidateWhileChildrenSyntax(step, workflow.Name);

        var syntheticWorkflowName = ReserveSyntheticWorkflowName(
            workflow.Name,
            step.Id,
            reservedWorkflowNames);
        var syntheticWorkflow = new WorkflowDefinition
        {
            Name = syntheticWorkflowName,
            Description = $"Synthetic while body for step '{step.Id}' in workflow '{workflow.Name}'.",
            Roles = workflow.Roles.Select(CloneRole).ToList(),
            Steps = step.Children!.Select(CloneStep).ToList(),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = workflow.Configuration.ClosedWorldMode,
            },
        };

        generatedWorkflows[syntheticWorkflowName] = NormalizeWorkflow(
            syntheticWorkflow,
            reservedWorkflowNames,
            generatedWorkflows);

        var parameters = new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal);
        parameters["step"] = "workflow_call";
        parameters["sub_param_workflow"] = syntheticWorkflowName;
        parameters["sub_param_lifecycle"] = "transient";

        return new StepDefinition
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole,
            Parameters = parameters,
            Next = step.Next,
            Children = null,
            Branches = step.Branches == null
                ? null
                : new Dictionary<string, string>(step.Branches, StringComparer.Ordinal),
            Retry = CloneRetry(step.Retry),
            OnError = CloneOnError(step.OnError),
            TimeoutMs = step.TimeoutMs,
        };
    }

    private static void ValidateWhileChildrenSyntax(StepDefinition step, string workflowName)
    {
        if (step.Parameters.ContainsKey("step"))
        {
            throw new InvalidOperationException(
                $"while.children in workflow '{workflowName}' step '{step.Id}' cannot be combined with parameters.step.");
        }

        if (step.Parameters.Keys.Any(key => key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"while.children in workflow '{workflowName}' step '{step.Id}' cannot be combined with sub_param_* parameters.");
        }
    }

    private static string ReserveSyntheticWorkflowName(
        string workflowName,
        string stepId,
        ISet<string> reservedWorkflowNames)
    {
        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var normalizedStepId = WorkflowRunIdNormalizer.NormalizeWorkflowName(stepId);
        var baseName = $"{SyntheticWorkflowPrefix}{normalizedWorkflowName}__{normalizedStepId}";
        var candidate = baseName;
        var suffix = 2;
        while (!reservedWorkflowNames.Add(candidate))
        {
            candidate = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }

        return candidate;
    }

    private static string SerializeWorkflow(WorkflowDefinition workflow)
    {
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = workflow.Name,
        };

        if (!string.IsNullOrWhiteSpace(workflow.Description))
            document["description"] = workflow.Description;
        if (workflow.Roles.Count > 0)
            document["roles"] = workflow.Roles.Select(ToYamlRole).ToList();
        if (workflow.Steps.Count > 0)
            document["steps"] = workflow.Steps.Select(ToYamlStep).ToList();
        if (workflow.Configuration.ClosedWorldMode)
        {
            document["configuration"] = new Dictionary<string, object?>
            {
                ["closed_world_mode"] = true,
            };
        }

        return Serializer.Serialize(document);
    }

    private static Dictionary<string, object?> ToYamlRole(RoleDefinition role)
    {
        var yaml = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = role.Id,
            ["name"] = role.Name,
        };

        AddIfNotBlank(yaml, "system_prompt", role.SystemPrompt);
        AddIfNotBlank(yaml, "provider", role.Provider);
        AddIfNotBlank(yaml, "model", role.Model);
        AddIfHasValue(yaml, "temperature", role.Temperature);
        AddIfHasValue(yaml, "max_tokens", role.MaxTokens);
        AddIfHasValue(yaml, "max_tool_rounds", role.MaxToolRounds);
        AddIfHasValue(yaml, "max_history_messages", role.MaxHistoryMessages);
        AddIfHasValue(yaml, "stream_buffer_capacity", role.StreamBufferCapacity);
        AddIfNotBlank(yaml, "event_modules", role.EventModules);
        AddIfNotBlank(yaml, "event_routes", role.EventRoutes);
        if (role.Connectors.Count > 0)
            yaml["connectors"] = role.Connectors.ToList();

        return yaml;
    }

    private static Dictionary<string, object?> ToYamlStep(StepDefinition step)
    {
        var yaml = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = step.Id,
            ["type"] = step.Type,
        };

        AddIfNotBlank(yaml, "target_role", step.TargetRole);
        if (step.Parameters.Count > 0)
            yaml["parameters"] = new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal);
        AddIfNotBlank(yaml, "next", step.Next);
        if (step.Children is { Count: > 0 })
            yaml["children"] = step.Children.Select(ToYamlStep).ToList();
        if (step.Branches is { Count: > 0 })
            yaml["branches"] = new Dictionary<string, string>(step.Branches, StringComparer.Ordinal);
        if (step.Retry != null)
        {
            yaml["retry"] = new Dictionary<string, object?>
            {
                ["max_attempts"] = step.Retry.MaxAttempts,
                ["backoff"] = step.Retry.Backoff,
                ["delay_ms"] = step.Retry.DelayMs,
            };
        }

        if (step.OnError != null)
        {
            var onError = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["strategy"] = step.OnError.Strategy,
            };
            AddIfNotBlank(onError, "fallback_step", step.OnError.FallbackStep);
            AddIfNotBlank(onError, "default_output", step.OnError.DefaultOutput);
            yaml["on_error"] = onError;
        }

        AddIfHasValue(yaml, "timeout_ms", step.TimeoutMs);
        return yaml;
    }

    private static void AddIfNotBlank(IDictionary<string, object?> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[key] = value;
    }

    private static void AddIfHasValue<T>(IDictionary<string, object?> target, string key, T? value)
        where T : struct
    {
        if (value.HasValue)
            target[key] = value.Value;
    }

    private static RoleDefinition CloneRole(RoleDefinition role) =>
        new()
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Temperature = role.Temperature,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            StreamBufferCapacity = role.StreamBufferCapacity,
            EventModules = role.EventModules,
            EventRoutes = role.EventRoutes,
            Connectors = role.Connectors.ToList(),
        };

    private static StepDefinition CloneStep(StepDefinition step) =>
        new()
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole,
            Parameters = new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal),
            Next = step.Next,
            Children = step.Children?.Select(CloneStep).ToList(),
            Branches = step.Branches == null
                ? null
                : new Dictionary<string, string>(step.Branches, StringComparer.Ordinal),
            Retry = CloneRetry(step.Retry),
            OnError = CloneOnError(step.OnError),
            TimeoutMs = step.TimeoutMs,
        };

    private static StepRetryPolicy? CloneRetry(StepRetryPolicy? retry) =>
        retry == null
            ? null
            : new StepRetryPolicy
            {
                MaxAttempts = retry.MaxAttempts,
                Backoff = retry.Backoff,
                DelayMs = retry.DelayMs,
            };

    private static StepErrorPolicy? CloneOnError(StepErrorPolicy? onError) =>
        onError == null
            ? null
            : new StepErrorPolicy
            {
                Strategy = onError.Strategy,
                FallbackStep = onError.FallbackStep,
                DefaultOutput = onError.DefaultOutput,
            };
}

internal readonly record struct WorkflowYamlBundleNormalizationResult(
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls);
