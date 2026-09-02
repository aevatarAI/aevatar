using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Workflows;

namespace Aevatar.Workflow.Infrastructure.Capabilities;

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
internal sealed class WorkflowInfrastructureCapabilitiesProvider : IWorkflowCapabilitiesPort
{
    private static readonly IReadOnlyDictionary<string, PrimitiveMetadataDescriptor> PrimitiveDescriptors =
        new Dictionary<string, PrimitiveMetadataDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["wait_signal"] = new(
                "Suspends workflow execution until an external signal arrives.",
                [
                    new PrimitiveParameterDescriptor("signal_name", "string", true, "Signal name used to resume this waiter."),
                    new PrimitiveParameterDescriptor("timeout_ms", "int", false, "Maximum wait duration in milliseconds, capped at 86400000."),
                ]),
            ["workflow_call"] = new(
                "Invokes another workflow definition as a sub-workflow.",
                [
                    new PrimitiveParameterDescriptor("workflow", "string", true, "Referenced workflow name."),
                    new PrimitiveParameterDescriptor("lifecycle", "string", false, "sync or async child lifecycle mode.", EnumValuesInput: ["sync", "async"]),
                ]),
            ["connector_call"] = new(
                "Invokes an external connector configured in connectors.json.",
                ConnectorParameterDescriptors()),
            ["secure_connector_call"] = new(
                "Invokes an external connector with secure payload handling.",
                ConnectorParameterDescriptors()),
            ["llm_call"] = new(
                "Runs an LLM role step and returns generated output.",
                [
                    new PrimitiveParameterDescriptor("prompt", "string", false, "Prompt template or prompt override."),
                ]),
            ["evaluate"] = new(
                "Runs an evaluation/judge step over current context.",
                [
                    new PrimitiveParameterDescriptor("criteria", "string", false, "Evaluation criteria."),
                ]),
            ["reflect"] = new(
                "Runs a reflection step to refine prior output.",
                [
                    new PrimitiveParameterDescriptor("prompt", "string", false, "Reflection prompt."),
                ]),
            ["self_reschedule"] = new(
                "Ensures a workflow schedule through the actor-owned scheduled-dispatch mainline.",
                [
                    new PrimitiveParameterDescriptor("schedule_id", "string", true, "Stable schedule id owned by ScheduledDispatchGAgent."),
                    new PrimitiveParameterDescriptor("cron_expression", "string", true, "Cron expression for future workflow runs."),
                    new PrimitiveParameterDescriptor("timezone", "string", false, "IANA or system timezone id.", DefaultValue: "UTC"),
                    new PrimitiveParameterDescriptor("workflow_name", "string", false, "Workflow service name when service_id is not supplied."),
                    new PrimitiveParameterDescriptor("service_id", "string", false, "Explicit workflow service id."),
                    new PrimitiveParameterDescriptor("scope_id", "string", true, "Scope or tenant id for the workflow service invocation."),
                    new PrimitiveParameterDescriptor("prompt", "string", false, "Prompt for the scheduled workflow chat request."),
                ]),
        };

    private static IReadOnlyList<PrimitiveParameterDescriptor> ConnectorParameterDescriptors() =>
    [
        new("connector", "string", true, "Connector name."),
        new("operation", "string", false, "Connector-specific operation or method."),
        new("approval.policy", "string", false, "Set to required to suspend before connector execution.", EnumValuesInput: ["required"]),
        new("approval.service_ref", "string", false, "NyxID connected-service reference bound to the approval."),
        new("approval.node_id", "string", false, "Required NyxID node routing identity."),
        new("approval.http_verb", "string", false, "Normalized external action HTTP verb."),
        new("approval.resource", "string", false, "Redacted external resource identity."),
        new("approval.permission_scope", "string", false, "Permission scope granted by this single action approval."),
        new("approval.expiration_seconds", "int", false, "Local approval validity window in seconds."),
        new(
            "approval.status_check_interval_seconds",
            "int",
            false,
            "Durable remote status check interval in seconds.",
            DefaultValue: $"{ConnectorApprovalOptionsDefinition.DefaultStatusCheckIntervalSeconds}"),
        new("approval.destructive", "bool", false, "Whether the action is destructive."),
        new("approval.team_id", "string", false, "Owning Studio team identity."),
        new("approval.member_id", "string", false, "Owning Studio member identity."),
        new("approval.workflow_id", "string", false, "Workflow definition identity."),
        new("approval.published_service_id", "string", false, "Published service runtime identity."),
        new("approval.policy_reason", "string", false, "Stable reason code for requiring approval."),
    ];

    private readonly IEnumerable<IWorkflowModulePack> _modulePacks;
    private readonly WorkflowCatalogReadModelQueryPort _catalogQueryPort;
    private readonly WorkflowCatalogReadModelMapper _mapper;

    public WorkflowInfrastructureCapabilitiesProvider(
        IEnumerable<IWorkflowModulePack> modulePacks,
        WorkflowCatalogReadModelQueryPort catalogQueryPort,
        WorkflowCatalogReadModelMapper mapper)
    {
        _modulePacks = modulePacks ?? throw new ArgumentNullException(nameof(modulePacks));
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    // Refactor (iter161-cluster-001 #1257-first):
    //   Old pattern: workflow capabilities were modeled as a startup artifact proto/partial surface after readmodel framing was removed.
    //   New principle: capabilities DTOs come from the narrow provider path and do not create a persisted artifact surface.
    public async Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var generatedAt = DateTimeOffset.UtcNow;
        var workflows = await _catalogQueryPort.QueryCatalogDocumentsAsync(ct);
        var workflowWatermark = workflows.Count == 0
            ? default
            : workflows.Max(workflow => workflow.UpdatedAt);

        return new WorkflowCapabilitiesDocument
        {
            SchemaVersion = "capabilities.v1",
            GeneratedAtUtc = generatedAt,
            ProjectionWatermark = generatedAt >= workflowWatermark ? generatedAt : workflowWatermark,
            Primitives = BuildPrimitiveCapabilities(),
            Connectors = BuildConnectorCapabilities(AevatarConnectorConfig.LoadConnectors()),
            Workflows = workflows
                .OrderBy(workflow => workflow.WorkflowName, StringComparer.OrdinalIgnoreCase)
                .Select(_mapper.ToCapabilityWorkflow)
                .ToList(),
        };
    }

    private List<WorkflowPrimitiveCapability> BuildPrimitiveCapabilities()
    {
        var aliasByCanonical = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var runtimeModuleByCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modulePacks = _modulePacks.ToList();
        if (modulePacks.Count == 0)
            modulePacks.Add(new WorkflowCoreModulePack());

        foreach (var registration in modulePacks.SelectMany(pack => pack.Modules))
        {
            foreach (var name in registration.Names)
            {
                var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(name);
                if (string.IsNullOrWhiteSpace(canonical))
                    continue;

                if (!aliasByCanonical.TryGetValue(canonical, out var aliases))
                {
                    aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    aliasByCanonical[canonical] = aliases;
                }

                aliases.Add(canonical);
                aliases.Add(name);
                runtimeModuleByCanonical.TryAdd(canonical, registration.ModuleType.Name);
            }
        }

        var canonicalTypes = new HashSet<string>(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var canonical in aliasByCanonical.Keys)
            canonicalTypes.Add(canonical);

        return canonicalTypes
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(canonical =>
            {
                var aliases = aliasByCanonical.TryGetValue(canonical, out var aliasSet)
                    ? aliasSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                    : [canonical];
                var descriptor = PrimitiveDescriptors.TryGetValue(canonical, out var knownDescriptor)
                    ? knownDescriptor
                    : new PrimitiveMetadataDescriptor($"Core workflow primitive `{canonical}`.", []);
                return new WorkflowPrimitiveCapability
                {
                    Name = canonical,
                    Aliases = aliases,
                    Category = InferPrimitiveCategory(canonical),
                    Description = descriptor.Description,
                    RuntimeModule = runtimeModuleByCanonical.GetValueOrDefault(canonical, string.Empty),
                    Parameters = descriptor.Parameters
                        .Select(parameter => new WorkflowPrimitiveParameterCapability
                        {
                            Name = parameter.Name,
                            Type = parameter.Type,
                            Required = parameter.Required,
                            Description = parameter.Description,
                            Default = parameter.DefaultValue,
                            Enum = parameter.EnumValues.ToList(),
                        })
                        .ToList(),
                };
            })
            .ToList();
    }

    private static List<WorkflowConnectorCapability> BuildConnectorCapabilities(
        IReadOnlyList<ConnectorConfigEntry> connectorEntries)
    {
        return connectorEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var normalizedType = (entry.Type ?? string.Empty).Trim();
                var typeKey = normalizedType.ToLowerInvariant();
                return new WorkflowConnectorCapability
                {
                    Name = entry.Name,
                    Type = normalizedType,
                    Enabled = entry.Enabled,
                    TimeoutMs = entry.TimeoutMs,
                    Retry = entry.Retry,
                    AllowedInputKeys = typeKey switch
                    {
                        "http" => NormalizeDistinct(entry.Http.AllowedInputKeys),
                        "cli" => NormalizeDistinct(entry.Cli.AllowedInputKeys),
                        "mcp" => NormalizeDistinct(entry.MCP.AllowedInputKeys),
                        "host_callback" => NormalizeDistinct(entry.HostCallback.AllowedInputKeys),
                        _ => [],
                    },
                    AllowedOperations = typeKey switch
                    {
                        "http" => NormalizeDistinct(entry.Http.AllowedMethods),
                        "cli" => NormalizeDistinct(entry.Cli.AllowedOperations),
                        "mcp" => NormalizeDistinct(entry.MCP.AllowedTools.Concat([entry.MCP.DefaultTool])),
                        "host_callback" => NormalizeDistinct(entry.HostCallback.AllowedOperations),
                        _ => [],
                    },
                    FixedArguments = typeKey switch
                    {
                        "cli" => NormalizeDistinct(entry.Cli.FixedArguments),
                        _ => [],
                    },
                };
            })
            .ToList();
    }

    private static List<string> NormalizeDistinct(IEnumerable<string>? values)
    {
        if (values == null)
            return [];

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string InferPrimitiveCategory(string canonicalType) =>
        canonicalType switch
        {
            "transform" or "assign" or "retrieve_facts" or "cache" => "data",
            "guard" or "conditional" or "switch" or "while" or "delay" or "wait_signal" or "checkpoint" or "workflow_loop" or "workflow_yaml_validate" => "control",
            "foreach" or "parallel" or "race" or "map_reduce" or "workflow_call" or "vote" or "dynamic_workflow" or "self_reschedule" => "composition",
            "llm_call" or "tool_call" or "evaluate" or "reflect" => "ai",
            "connector_call" or "secure_connector_call" or "emit" => "integration",
            "human_input" or "human_approval" or "secure_input" => "human",
            _ => "general",
        };

    private sealed record PrimitiveMetadataDescriptor(
        string Description,
        IReadOnlyList<PrimitiveParameterDescriptor> Parameters);

    private sealed record PrimitiveParameterDescriptor(
        string Name,
        string Type,
        bool Required,
        string Description,
        string DefaultValue = "",
        IReadOnlyList<string>? EnumValuesInput = null)
    {
        public IReadOnlyList<string> EnumValues { get; } = EnumValuesInput ?? [];
    }
}
