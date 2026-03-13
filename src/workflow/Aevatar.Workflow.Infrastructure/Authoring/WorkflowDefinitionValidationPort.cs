using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Infrastructure.Authoring;

internal sealed class WorkflowDefinitionValidationPort : IWorkflowDefinitionValidationPort
{
    private readonly IReadOnlyList<IWorkflowModulePack> _modulePacks;
    private readonly IEventModuleFactory? _moduleFactory;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly WorkflowParser _parser = new();

    public WorkflowDefinitionValidationPort(
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionRegistry workflowRegistry,
        IEventModuleFactory? moduleFactory = null)
    {
        _modulePacks = modulePacks?.ToList() ?? throw new ArgumentNullException(nameof(modulePacks));
        _workflowRegistry = workflowRegistry ?? throw new ArgumentNullException(nameof(workflowRegistry));
        _moduleFactory = moduleFactory;
    }

    public Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
        PlaygroundWorkflowParseRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return Task.FromResult(new PlaygroundWorkflowParseResult
            {
                Valid = false,
                Error = "Empty YAML",
                Errors = ["Empty YAML"],
            });
        }

        try
        {
            var definition = _parser.Parse(request.Yaml);
            var errors = ValidateWorkflowDefinitionForRuntime(definition);
            return Task.FromResult(new PlaygroundWorkflowParseResult
            {
                Valid = errors.Count == 0,
                Error = errors.Count == 0 ? null : string.Join("; ", errors),
                Errors = errors,
                Definition = MapDefinition(definition),
                Edges = ComputeEdges(definition),
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PlaygroundWorkflowParseResult
            {
                Valid = false,
                Error = ex.Message,
                Errors = [ex.Message],
            });
        }
    }

    private List<string> ValidateWorkflowDefinitionForRuntime(WorkflowDefinition definition)
    {
        var knownStepTypes = _modulePacks.Count > 0
            ? WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                _modulePacks.SelectMany(pack => pack.Modules).SelectMany(module => module.Names))
            : WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);

        if (_moduleFactory != null)
        {
            foreach (var stepType in EnumerateReferencedStepTypes(definition.Steps))
            {
                var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
                if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                    continue;

                if (_moduleFactory.TryCreate(canonical, out _))
                    knownStepTypes.Add(canonical);
            }
        }

        var availableWorkflowNames = _workflowRegistry.GetNames()
            .Append(definition.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return WorkflowValidator.Validate(
            definition,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = knownStepTypes,
            },
            availableWorkflowNames);
    }

    private static WorkflowAuthoringDefinition MapDefinition(WorkflowDefinition definition)
    {
        return new WorkflowAuthoringDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            ClosedWorldMode = definition.Configuration.ClosedWorldMode,
            Roles = definition.Roles.Select(MapRole).ToList(),
            Steps = definition.Steps.Select(MapStep).ToList(),
        };
    }

    private static WorkflowAuthoringRole MapRole(RoleDefinition role)
    {
        return new WorkflowAuthoringRole
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Temperature = role.Temperature is null ? null : (float)role.Temperature.Value,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            StreamBufferCapacity = role.StreamBufferCapacity,
            EventModules = SplitCsv(role.EventModules),
            EventRoutes = role.EventRoutes ?? string.Empty,
            Connectors = role.Connectors.ToList(),
        };
    }

    private static WorkflowAuthoringStep MapStep(StepDefinition step)
    {
        return new WorkflowAuthoringStep
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole ?? string.Empty,
            Parameters = step.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Next = step.Next,
            Branches = step.Branches?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) ?? [],
            Children = step.Children?.Select(MapStep).ToList() ?? [],
            Retry = step.Retry == null
                ? null
                : new WorkflowAuthoringRetryPolicy
                {
                    MaxAttempts = step.Retry.MaxAttempts,
                    Backoff = step.Retry.Backoff,
                    DelayMs = step.Retry.DelayMs,
                },
            OnError = step.OnError == null
                ? null
                : new WorkflowAuthoringErrorPolicy
                {
                    Strategy = step.OnError.Strategy,
                    FallbackStep = step.OnError.FallbackStep,
                    DefaultOutput = step.OnError.DefaultOutput,
                },
            TimeoutMs = step.TimeoutMs,
        };
    }

    private static List<string> SplitCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WorkflowAuthoringEdge> ComputeEdges(WorkflowDefinition definition)
    {
        var allStepIds = EnumerateAllSteps(definition.Steps)
            .Select(step => step.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var edges = new List<WorkflowAuthoringEdge>();
        AppendEdges(definition.Steps, allStepIds, edges);
        return edges;
    }

    private static void AppendEdges(
        IReadOnlyList<StepDefinition> steps,
        ISet<string> allStepIds,
        ICollection<WorkflowAuthoringEdge> edges)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Branches is { Count: > 0 })
            {
                foreach (var (label, targetId) in step.Branches)
                {
                    if (!string.IsNullOrWhiteSpace(targetId) && allStepIds.Contains(targetId))
                    {
                        edges.Add(new WorkflowAuthoringEdge
                        {
                            From = step.Id,
                            To = targetId,
                            Label = label,
                        });
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.Next) && allStepIds.Contains(step.Next))
            {
                edges.Add(new WorkflowAuthoringEdge
                {
                    From = step.Id,
                    To = step.Next,
                });
            }
            else if (i + 1 < steps.Count)
            {
                edges.Add(new WorkflowAuthoringEdge
                {
                    From = step.Id,
                    To = steps[i + 1].Id,
                });
            }

            if (step.Children is not { Count: > 0 })
                continue;

            foreach (var child in step.Children)
            {
                edges.Add(new WorkflowAuthoringEdge
                {
                    From = step.Id,
                    To = child.Id,
                    Label = "child",
                });
            }

            AppendEdges(step.Children, allStepIds, edges);
        }
    }

    private static IEnumerable<string> EnumerateReferencedStepTypes(
        IReadOnlyList<StepDefinition> steps)
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

            if (step.Children is not { Count: > 0 })
                continue;

            foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                yield return childType;
        }
    }

    private static IEnumerable<StepDefinition> EnumerateAllSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Children is not { Count: > 0 })
                continue;

            foreach (var child in EnumerateAllSteps(step.Children))
                yield return child;
        }
    }
}
