using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions.Primitives;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Workflows;

namespace Aevatar.Workflow.Infrastructure.Capabilities;

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
internal sealed class WorkflowInfrastructureCapabilitiesProvider : IWorkflowCapabilitiesPort
{
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
        var descriptorByCanonical = new Dictionary<string, WorkflowPrimitiveDefinition>(StringComparer.OrdinalIgnoreCase);
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
                if (registration.Descriptor is { } contributedDescriptor)
                    descriptorByCanonical[canonical] = contributedDescriptor;
            }
        }

        var canonicalTypes = new HashSet<string>(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var canonical in aliasByCanonical.Keys)
            canonicalTypes.Add(canonical);

        foreach (var canonical in canonicalTypes)
        {
            if (!aliasByCanonical.TryGetValue(canonical, out var acceptedNames))
                aliasByCanonical[canonical] = acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
            acceptedNames.UnionWith(WorkflowPrimitiveCatalog.GetAliases(canonical));
        }
        var acceptedStepTypes = aliasByCanonical.Values.SelectMany(names => names)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return canonicalTypes
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(canonical =>
            {
                var aliases = aliasByCanonical.TryGetValue(canonical, out var aliasSet)
                    ? aliasSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                    : [canonical];
                var descriptor = descriptorByCanonical.GetValueOrDefault(canonical)
                    ?? WorkflowPrimitiveDefinitions.Find(canonical)
                    ?? new WorkflowPrimitiveDefinition($"Workflow primitive `{canonical}`.", []);
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
                            Type = parameter.Type switch
                            {
                                WorkflowPrimitiveParameterType.String => "string",
                                WorkflowPrimitiveParameterType.Integer => "int",
                                WorkflowPrimitiveParameterType.Number => "number",
                                WorkflowPrimitiveParameterType.Boolean => "bool",
                                WorkflowPrimitiveParameterType.Array => "array",
                                WorkflowPrimitiveParameterType.Object => "object",
                                _ => throw new ArgumentOutOfRangeException(nameof(parameter.Type)),
                            },
                            Required = parameter.Required,
                            Description = parameter.Description,
                            Default = parameter.DefaultValue,
                            Enum = parameter.UsesPrimitiveNames
                                ? acceptedStepTypes.ToList()
                                : parameter.EnumValues?.ToList() ?? [],
                            Label = parameter.Label,
                            Control = ResolveControl(parameter),
                            Placeholder = parameter.Placeholder,
                            Minimum = parameter.Minimum,
                            Maximum = parameter.Maximum,
                            Integer = parameter.Type == WorkflowPrimitiveParameterType.Integer,
                        })
                        .ToList(),
                };
            })
            .ToList();
    }

    private static WorkflowPrimitiveParameterControl ResolveControl(WorkflowPrimitiveParameterDefinition parameter)
    {
        if (parameter.Control != WorkflowPrimitiveParameterControl.Auto)
            return parameter.Control;
        if (parameter.UsesPrimitiveNames || parameter.EnumValues is { Count: > 0 })
            return WorkflowPrimitiveParameterControl.Select;
        return parameter.Type switch
        {
            WorkflowPrimitiveParameterType.Integer or WorkflowPrimitiveParameterType.Number => WorkflowPrimitiveParameterControl.Number,
            WorkflowPrimitiveParameterType.Boolean => WorkflowPrimitiveParameterControl.Boolean,
            WorkflowPrimitiveParameterType.Array or WorkflowPrimitiveParameterType.Object => WorkflowPrimitiveParameterControl.Json,
            _ => WorkflowPrimitiveParameterControl.Text,
        };
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

}
