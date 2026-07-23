using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Workflows;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowCatalogCurrentStateProjector
    : ICurrentStateProjectionMaterializer<WorkflowBindingProjectionContext>
{
    private static readonly HashSet<string> LlmLikeStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "llm_call",
        "evaluate",
        "reflect",
        "tool_call",
        "human_input",
        "secure_input",
        "human_approval",
        "wait_signal",
        "connector_call",
        "secure_connector_call",
    };

    private readonly IProjectionWriteDispatcher<WorkflowCatalogCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowCatalogCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowCatalogCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // Refactor (iter46/issue-871-workflow-file-catalog-query-port):
    //   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
    //   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
    public async ValueTask ProjectAsync(
        WorkflowBindingProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<WorkflowState>(
                envelope,
                out var published,
                out var stateEvent,
                out var state) ||
            published == null ||
            stateEvent?.EventData == null ||
            state == null ||
            !stateEvent.EventData.Is(BindWorkflowDefinitionEvent.Descriptor))
        {
            return;
        }

        // Scope-owned definitions (bound during service invocation/activation) carry a non-empty
        // ScopeId. This readmodel is the globally shared public template library read via
        // aevatar_list_workflow_templates / aevatar_get_workflow_template; letting a scope-owned
        // definition in would expose one tenant's workflow YAML and role system prompts to all
        // callers, and same-named scoped definitions would clobber each other under the name-keyed
        // document id. Team-owned workflows are served by the scope-filtered Studio member readmodel.
        if (!string.IsNullOrWhiteSpace(state.ScopeId))
            return;

        var workflowName = NormalizeWorkflowName(state.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName) ||
            string.IsNullOrWhiteSpace(state.WorkflowYaml))
        {
            return;
        }

        var document = BuildDocument(
            context.RootActorId,
            workflowName,
            state,
            stateEvent.EventId ?? string.Empty,
            stateEvent.Version,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow));
        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static WorkflowCatalogCurrentStateDocument BuildDocument(
        string actorId,
        string workflowName,
        WorkflowState state,
        string eventId,
        long stateVersion,
        DateTimeOffset updatedAt)
    {
        var definition = new WorkflowParser().Parse(state.WorkflowYaml);
        var primitives = EnumerateReferencedStepTypes(definition.Steps)
            .Select(WorkflowPrimitiveCatalog.ToCanonicalType)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var category = primitives.Any(primitive => LlmLikeStepTypes.Contains(primitive))
            ? "llm"
            : "deterministic";
        var source = string.IsNullOrWhiteSpace(state.SourceKind)
            ? "builtin"
            : state.SourceKind.Trim();
        var classification = WorkflowCatalogClassificationPolicy.Classify(workflowName, source, category);
        var requiredConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connectorName in definition.Roles.SelectMany(role => role.Connectors))
            AddIfNotWhitespace(requiredConnectors, connectorName);

        var workflowCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capabilitySteps = new List<WorkflowCatalogStepReadModel>();
        foreach (var step in EnumerateAllSteps(definition.Steps))
        {
            var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
            capabilitySteps.Add(BuildStep(step));

            if (string.Equals(canonicalType, "connector_call", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonicalType, "secure_connector_call", StringComparison.OrdinalIgnoreCase))
            {
                if (step.Parameters.TryGetValue("connector", out var connector))
                    AddIfNotWhitespace(requiredConnectors, connector);
            }

            if (string.Equals(canonicalType, "workflow_call", StringComparison.OrdinalIgnoreCase) &&
                step.Parameters.TryGetValue("workflow", out var calledWorkflow))
            {
                AddIfNotWhitespace(workflowCalls, calledWorkflow);
            }
        }

        return new WorkflowCatalogCurrentStateDocument
        {
            Id = workflowName,
            ActorId = actorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = updatedAt,
            WorkflowName = workflowName,
            WorkflowYaml = state.WorkflowYaml ?? string.Empty,
            Description = definition.Description ?? string.Empty,
            Category = category,
            Group = classification.Group,
            GroupLabel = classification.GroupLabel,
            SortOrder = classification.SortOrder,
            Source = source,
            SourceLabel = classification.SourceLabel,
            ShowInLibrary = classification.ShowInLibrary,
            IsPrimitiveExample = classification.IsPrimitiveExample,
            RequiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition),
            Primitives = primitives,
            ClosedWorldMode = definition.Configuration.ClosedWorldMode,
            Roles = definition.Roles.Select(BuildRole).ToList(),
            Steps = capabilitySteps,
            Edges = ComputeEdges(definition),
            RequiredConnectors = requiredConnectors
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AuthorizationDependencies = state.AuthorizationDependencies?.Clone(),
            WorkflowCalls = workflowCalls
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static WorkflowCatalogRoleReadModel BuildRole(RoleDefinition role) =>
        new()
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider ?? string.Empty,
            Model = role.Model ?? string.Empty,
            Temperature = role.Temperature is null ? null : (float)role.Temperature.Value,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            EventModules = SplitCsv(role.EventModules),
            EventRoutes = role.EventRoutes ?? string.Empty,
            Connectors = role.Connectors.ToList(),
        };

    private static WorkflowCatalogStepReadModel BuildStep(StepDefinition step) =>
        new()
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole ?? string.Empty,
            Parameters = step.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Next = step.Next ?? string.Empty,
            Branches = step.Branches?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) ?? [],
            Children = step.Children?.Select(child => new WorkflowCatalogChildStepReadModel
            {
                Id = child.Id,
                Type = child.Type,
                TargetRole = child.TargetRole ?? string.Empty,
            }).ToList() ?? [],
        };

    private static List<WorkflowCatalogEdgeReadModel> ComputeEdges(WorkflowDefinition definition)
    {
        var edges = new List<WorkflowCatalogEdgeReadModel>();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            if (step.Branches is { Count: > 0 })
            {
                foreach (var (label, targetId) in step.Branches)
                {
                    if (definition.GetStep(targetId) != null)
                    {
                        edges.Add(new WorkflowCatalogEdgeReadModel
                        {
                            From = step.Id,
                            To = targetId,
                            Label = label,
                        });
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.Next))
            {
                if (definition.GetStep(step.Next) != null)
                {
                    edges.Add(new WorkflowCatalogEdgeReadModel
                    {
                        From = step.Id,
                        To = step.Next,
                    });
                }
            }
            else if (i + 1 < definition.Steps.Count)
            {
                edges.Add(new WorkflowCatalogEdgeReadModel
                {
                    From = step.Id,
                    To = definition.Steps[i + 1].Id,
                });
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var child in step.Children)
                {
                    edges.Add(new WorkflowCatalogEdgeReadModel
                    {
                        From = step.Id,
                        To = child.Id,
                        Label = "child",
                    });
                }
            }
        }

        return edges;
    }

    private static IEnumerable<StepDefinition> EnumerateAllSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Children is { Count: > 0 })
            {
                foreach (var child in EnumerateAllSteps(step.Children))
                    yield return child;
            }
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

    private static void AddIfNotWhitespace(ISet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            set.Add(value.Trim());
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

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName)
            ? string.Empty
            : workflowName.Trim();
}
