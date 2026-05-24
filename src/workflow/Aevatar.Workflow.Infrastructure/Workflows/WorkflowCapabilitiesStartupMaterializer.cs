using Aevatar.Configuration;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Runtime.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Workflows;

// Refactor (iter72/cluster-072-workflow-closed-world-false-capability):
//   Old pattern: ClosedWorldBlocked flag retained as always-false compatibility field
//   New principle: Removed dead capability flag; output describes available primitives only
internal sealed class WorkflowCapabilitiesStartupMaterializer
{
    public const string ArtifactId = "workflow-capabilities";

    private static readonly IReadOnlyDictionary<string, PrimitiveMetadataDescriptor> PrimitiveDescriptors =
        new Dictionary<string, PrimitiveMetadataDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["wait_signal"] = new(
                "Suspends workflow execution until an external signal arrives.",
                [
                    new PrimitiveParameterDescriptor("signal_name", "string", true, "Signal name used to resume this waiter."),
                    new PrimitiveParameterDescriptor("timeout_ms", "int", false, "Maximum wait duration in milliseconds."),
                ]),
            ["workflow_call"] = new(
                "Invokes another workflow definition as a sub-workflow.",
                [
                    new PrimitiveParameterDescriptor("workflow", "string", true, "Referenced workflow name."),
                    new PrimitiveParameterDescriptor("lifecycle", "string", false, "sync or async child lifecycle mode.", EnumValuesInput: ["sync", "async"]),
                ]),
            ["connector_call"] = new(
                "Invokes an external connector configured in connectors.json.",
                [
                    new PrimitiveParameterDescriptor("connector", "string", true, "Connector name."),
                    new PrimitiveParameterDescriptor("operation", "string", false, "Connector-specific operation or method."),
                ]),
            ["secure_connector_call"] = new(
                "Invokes an external connector with secure payload handling.",
                [
                    new PrimitiveParameterDescriptor("connector", "string", true, "Connector name."),
                    new PrimitiveParameterDescriptor("operation", "string", false, "Connector-specific operation or method."),
                ]),
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
        };

    private readonly IProjectionWriteDispatcher<WorkflowCapabilitiesStartupArtifact> _writeDispatcher;
    private readonly IEnumerable<IWorkflowModulePack> _modulePacks;

    public WorkflowCapabilitiesStartupMaterializer(
        IProjectionWriteDispatcher<WorkflowCapabilitiesStartupArtifact> writeDispatcher,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _modulePacks = modulePacks ?? throw new ArgumentNullException(nameof(modulePacks));
    }

    // Refactor (iter46/issue-871-workflow-file-catalog-query-port):
    //   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
    //   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
    public async Task MaterializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var artifact = new WorkflowCapabilitiesStartupArtifact
        {
            Id = ArtifactId,
            GeneratedAtUtc = now,
            SchemaVersion = "capabilities.v1",
            Primitives = BuildPrimitiveCapabilities(),
            Connectors = BuildConnectorCapabilities(AevatarConnectorConfig.LoadConnectors()),
        };

        await _writeDispatcher.UpsertAsync(artifact, ct);
    }

    private List<WorkflowPrimitiveCapabilityReadModel> BuildPrimitiveCapabilities()
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
                return new WorkflowPrimitiveCapabilityReadModel
                {
                    Name = canonical,
                    Aliases = aliases,
                    Category = InferPrimitiveCategory(canonical),
                    Description = descriptor.Description,
                    RuntimeModule = runtimeModuleByCanonical.GetValueOrDefault(canonical, string.Empty),
                    Parameters = descriptor.Parameters
                        .Select(parameter => new WorkflowPrimitiveParameterCapabilityReadModel
                        {
                            Name = parameter.Name,
                            Type = parameter.Type,
                            Required = parameter.Required,
                            Description = parameter.Description,
                            DefaultValue = parameter.DefaultValue,
                            Enum = parameter.EnumValues.ToList(),
                        })
                        .ToList(),
                };
            })
            .ToList();
    }

    private static List<WorkflowConnectorCapabilityReadModel> BuildConnectorCapabilities(
        IReadOnlyList<ConnectorConfigEntry> connectorEntries)
    {
        return connectorEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var normalizedType = (entry.Type ?? string.Empty).Trim();
                var typeKey = normalizedType.ToLowerInvariant();
                return new WorkflowConnectorCapabilityReadModel
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
                        _ => [],
                    },
                    AllowedOperations = typeKey switch
                    {
                        "http" => NormalizeDistinct(entry.Http.AllowedMethods),
                        "cli" => NormalizeDistinct(entry.Cli.AllowedOperations),
                        "mcp" => NormalizeDistinct(entry.MCP.AllowedTools.Concat([entry.MCP.DefaultTool])),
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
            "foreach" or "parallel" or "race" or "map_reduce" or "workflow_call" or "vote" or "dynamic_workflow" => "composition",
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
