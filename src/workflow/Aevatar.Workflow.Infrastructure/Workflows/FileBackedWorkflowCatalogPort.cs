using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class FileBackedWorkflowCatalogPort : IWorkflowCatalogPort, IWorkflowCapabilitiesPort
{
    private static readonly TimeSpan WorkflowFileDiscoveryCacheTtl = TimeSpan.FromSeconds(5);

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

    private static readonly IReadOnlyDictionary<string, PrimitiveMetadataDescriptor> PrimitiveMetadata =
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

    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IOptions<WorkflowDefinitionFileSourceOptions> _options;
    private readonly WorkflowParser _parser = new();
    private readonly ILogger<FileBackedWorkflowCatalogPort> _logger;
    private readonly object _cacheLock = new();
    private FileDiscoveryCacheEntry? _workflowFileDiscoveryCache;
    private readonly Dictionary<string, ParsedWorkflowCacheEntry> _parsedWorkflowCache = new(StringComparer.OrdinalIgnoreCase);

    public FileBackedWorkflowCatalogPort(
        IWorkflowDefinitionRegistry workflowRegistry,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<FileBackedWorkflowCatalogPort>? logger = null)
    {
        _workflowRegistry = workflowRegistry;
        _options = options;
        _logger = logger ?? NullLogger<FileBackedWorkflowCatalogPort>.Instance;
    }

    public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog()
    {
        var fileEntries = DiscoverWorkflowFiles();
        var items = new List<WorkflowCatalogItem>();

        foreach (var workflowName in _workflowRegistry.GetNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var yaml = _workflowRegistry.GetYaml(workflowName);
            if (string.IsNullOrWhiteSpace(yaml))
                continue;

            fileEntries.TryGetValue(workflowName, out var fileEntry);
            items.Add(BuildCatalogItem(workflowName, yaml, fileEntry));
        }

        return items
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        var normalizedName = workflowName.Trim();
        var yaml = _workflowRegistry.GetYaml(normalizedName);
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        if (!TryGetCachedDefinition(normalizedName, yaml, out var definition) || definition == null)
            return null;

        var fileEntries = DiscoverWorkflowFiles();
        fileEntries.TryGetValue(normalizedName, out var fileEntry);
        var catalogItem = BuildCatalogItem(normalizedName, yaml, fileEntry);

        return new WorkflowCatalogItemDetail
        {
            Catalog = catalogItem,
            Yaml = yaml,
            Definition = BuildDefinition(definition),
            Edges = ComputeEdges(definition),
        };
    }

    public WorkflowCapabilitiesDocument GetCapabilities()
    {
        var fileEntries = DiscoverWorkflowFiles();
        var connectorEntries = AevatarConnectorConfig.LoadConnectors();
        return new WorkflowCapabilitiesDocument
        {
            SchemaVersion = "capabilities.v1",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Primitives = BuildPrimitiveCapabilities(),
            Connectors = BuildConnectorCapabilities(connectorEntries),
            Workflows = BuildWorkflowCapabilities(fileEntries),
        };
    }

    private WorkflowCatalogItem BuildCatalogItem(
        string workflowName,
        string yaml,
        WorkflowFileEntry? fileEntry)
    {
        var source = fileEntry?.SourceKind ?? "builtin";

        string description = string.Empty;
        string category = "deterministic";
        var requiresLlmProvider = false;
        var primitives = new List<string>();

        if (TryGetCachedDefinition(workflowName, yaml, out var definition) && definition != null)
        {
            description = definition.Description ?? string.Empty;
            primitives = definition.Steps
                .Select(step => WorkflowPrimitiveCatalog.ToCanonicalType(step.Type))
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            requiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition);
            category = primitives.Any(primitive => LlmLikeStepTypes.Contains(primitive))
                ? "llm"
                : "deterministic";
        }

        var classification = WorkflowLibraryClassifier.Classify(workflowName, source, category);

        return new WorkflowCatalogItem
        {
            Name = workflowName,
            Description = description,
            Category = category,
            Group = classification.Group,
            GroupLabel = classification.GroupLabel,
            SortOrder = classification.SortOrder,
            Source = source,
            SourceLabel = classification.SourceLabel,
            ShowInLibrary = classification.ShowInLibrary,
            IsPrimitiveExample = classification.IsPrimitiveExample,
            RequiresLlmProvider = requiresLlmProvider,
            Primitives = primitives,
        };
    }

    private List<WorkflowPrimitiveCapability> BuildPrimitiveCapabilities()
    {
        var aliasByCanonical = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var runtimeModuleByCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modulePack = new WorkflowCoreModulePack();
        foreach (var registration in modulePack.Modules)
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
                var metadata = PrimitiveMetadata.TryGetValue(canonical, out var descriptor)
                    ? descriptor
                    : new PrimitiveMetadataDescriptor(
                        $"Core workflow primitive `{canonical}`.",
                        []);
                return new WorkflowPrimitiveCapability
                {
                    Name = canonical,
                    Aliases = aliases,
                    Category = InferPrimitiveCategory(canonical),
                    Description = metadata.Description,
                    ClosedWorldBlocked = WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonical),
                    RuntimeModule = runtimeModuleByCanonical.GetValueOrDefault(canonical, string.Empty),
                    Parameters = metadata.Parameters
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
                var allowedInputKeys = typeKey switch
                {
                    "http" => NormalizeDistinct(entry.Http.AllowedInputKeys),
                    "cli" => NormalizeDistinct(entry.Cli.AllowedInputKeys),
                    "mcp" => NormalizeDistinct(entry.MCP.AllowedInputKeys),
                    _ => [],
                };
                var allowedOperations = typeKey switch
                {
                    "http" => NormalizeDistinct(entry.Http.AllowedMethods),
                    "cli" => NormalizeDistinct(entry.Cli.AllowedOperations),
                    "mcp" => NormalizeDistinct(entry.MCP.AllowedTools.Concat([entry.MCP.DefaultTool])),
                    _ => [],
                };
                var fixedArguments = typeKey switch
                {
                    "cli" => NormalizeDistinct(entry.Cli.FixedArguments),
                    _ => [],
                };

                return new WorkflowConnectorCapability
                {
                    Name = entry.Name,
                    Type = normalizedType,
                    Enabled = entry.Enabled,
                    TimeoutMs = entry.TimeoutMs,
                    Retry = entry.Retry,
                    AllowedInputKeys = allowedInputKeys,
                    AllowedOperations = allowedOperations,
                    FixedArguments = fixedArguments,
                };
            })
            .ToList();
    }

    private List<WorkflowCapabilityWorkflow> BuildWorkflowCapabilities(
        IReadOnlyDictionary<string, WorkflowFileEntry> fileEntries)
    {
        var workflows = new List<WorkflowCapabilityWorkflow>();
        foreach (var workflowName in _workflowRegistry.GetNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var yaml = _workflowRegistry.GetYaml(workflowName);
            if (string.IsNullOrWhiteSpace(yaml))
                continue;

            fileEntries.TryGetValue(workflowName, out var fileEntry);
            var source = fileEntry?.SourceKind ?? "builtin";
            _ = TryGetCachedDefinition(workflowName, yaml, out var definition);

            if (definition == null)
            {
                workflows.Add(new WorkflowCapabilityWorkflow
                {
                    Name = workflowName,
                    Source = source,
                });
                continue;
            }

            var primitives = EnumerateReferencedStepTypes(definition.Steps)
                .Select(WorkflowPrimitiveCatalog.ToCanonicalType)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requiredConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var connectorName in definition.Roles.SelectMany(role => role.Connectors))
                AddIfNotWhitespace(requiredConnectors, connectorName);

            var workflowCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var steps = new List<WorkflowCapabilityWorkflowStep>();
            foreach (var step in EnumerateAllSteps(definition.Steps))
            {
                var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
                steps.Add(new WorkflowCapabilityWorkflowStep
                {
                    Id = step.Id,
                    Type = canonicalType,
                    Next = step.Next ?? string.Empty,
                });

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

            workflows.Add(new WorkflowCapabilityWorkflow
            {
                Name = workflowName,
                Description = definition.Description ?? string.Empty,
                Source = source,
                ClosedWorldMode = definition.Configuration.ClosedWorldMode,
                RequiresLlmProvider = WorkflowLlmRuntimePolicy.RequiresLlmProvider(definition),
                Primitives = primitives,
                RequiredConnectors = requiredConnectors
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                WorkflowCalls = workflowCalls
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Steps = steps,
            });
        }

        return workflows;
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

    private IReadOnlyDictionary<string, WorkflowFileEntry> DiscoverWorkflowFiles()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_cacheLock)
        {
            if (_workflowFileDiscoveryCache is { } cache &&
                cache.ExpiresAtUtc > now)
            {
                return cache.Entries;
            }
        }

        var entries = DiscoverWorkflowFilesCore();
        lock (_cacheLock)
        {
            _workflowFileDiscoveryCache = new FileDiscoveryCacheEntry(
                entries,
                now.Add(WorkflowFileDiscoveryCacheTtl));
        }

        return entries;
    }

    private Dictionary<string, WorkflowFileEntry> DiscoverWorkflowFilesCore()
    {
        var entries = new Dictionary<string, WorkflowFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in ResolveNormalizedWorkflowDirectories())
        {
            var sourceKind = ResolveSourceKind(directory);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.*")
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    entries[name] = new WorkflowFileEntry(name.Trim(), file, sourceKind);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate workflow files from directory '{WorkflowDirectory}'.", directory);
            }
        }

        return entries;
    }

    private IReadOnlyList<string> ResolveNormalizedWorkflowDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawDirectory in _options.Value.WorkflowDirectories)
        {
            if (string.IsNullOrWhiteSpace(rawDirectory))
                continue;

            try
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawDirectory));
                if (Directory.Exists(normalized))
                    directories.Add(normalized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize workflow directory '{WorkflowDirectory}'.", rawDirectory);
            }
        }

        return directories
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryGetCachedDefinition(
        string workflowName,
        string yaml,
        out WorkflowDefinition? definition)
    {
        definition = null;
        if (string.IsNullOrWhiteSpace(workflowName) || string.IsNullOrWhiteSpace(yaml))
            return false;

        var normalizedWorkflowName = workflowName.Trim();
        lock (_cacheLock)
        {
            if (_parsedWorkflowCache.TryGetValue(normalizedWorkflowName, out var cached) &&
                string.Equals(cached.Yaml, yaml, StringComparison.Ordinal))
            {
                definition = cached.Definition;
                return definition != null;
            }
        }

        WorkflowDefinition? parsed = null;
        try
        {
            parsed = _parser.Parse(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse workflow yaml for '{WorkflowName}'.", normalizedWorkflowName);
        }

        lock (_cacheLock)
        {
            _parsedWorkflowCache[normalizedWorkflowName] = new ParsedWorkflowCacheEntry(yaml, parsed);
        }

        definition = parsed;
        return definition != null;
    }

    private static WorkflowCatalogDefinition BuildDefinition(WorkflowDefinition definition)
    {
        return new WorkflowCatalogDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            ClosedWorldMode = definition.Configuration.ClosedWorldMode,
            Roles = definition.Roles.Select(BuildRole).ToList(),
            Steps = definition.Steps.Select(BuildStep).ToList(),
        };
    }

    private static WorkflowCatalogRole BuildRole(RoleDefinition role)
    {
        return new WorkflowCatalogRole
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
            StreamBufferCapacity = role.StreamBufferCapacity,
            EventModules = SplitCsv(role.EventModules),
            EventRoutes = role.EventRoutes ?? string.Empty,
            Connectors = role.Connectors.ToList(),
        };
    }

    private static WorkflowCatalogStep BuildStep(StepDefinition step)
    {
        return new WorkflowCatalogStep
        {
            Id = step.Id,
            Type = step.Type,
            TargetRole = step.TargetRole ?? string.Empty,
            Parameters = step.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Next = step.Next ?? string.Empty,
            Branches = step.Branches?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) ?? [],
            Children = step.Children?.Select(child => new WorkflowCatalogChildStep
            {
                Id = child.Id,
                Type = child.Type,
                TargetRole = child.TargetRole ?? string.Empty,
            }).ToList() ?? [],
        };
    }

    private static List<WorkflowCatalogEdge> ComputeEdges(WorkflowDefinition definition)
    {
        var edges = new List<WorkflowCatalogEdge>();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            if (step.Branches is { Count: > 0 })
            {
                foreach (var (label, targetId) in step.Branches)
                {
                    if (definition.GetStep(targetId) != null)
                    {
                        edges.Add(new WorkflowCatalogEdge
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
                    edges.Add(new WorkflowCatalogEdge
                    {
                        From = step.Id,
                        To = step.Next,
                    });
                }
            }
            else if (i + 1 < definition.Steps.Count)
            {
                edges.Add(new WorkflowCatalogEdge
                {
                    From = step.Id,
                    To = definition.Steps[i + 1].Id,
                });
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var child in step.Children)
                {
                    edges.Add(new WorkflowCatalogEdge
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

    private static string ResolveSourceKind(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(AevatarPaths.Workflows)), StringComparison.OrdinalIgnoreCase))
            return "home";

        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(AevatarPaths.RepoRootWorkflows)), StringComparison.OrdinalIgnoreCase))
            return "repo";

        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "workflows"))), StringComparison.OrdinalIgnoreCase))
            return "cwd";

        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workflows"))), StringComparison.OrdinalIgnoreCase))
            return "app";

        if (normalized.EndsWith($"{Path.DirectorySeparatorChar}turing-completeness", StringComparison.OrdinalIgnoreCase))
            return "turing";

        return "file";
    }

    private sealed record FileDiscoveryCacheEntry(
        IReadOnlyDictionary<string, WorkflowFileEntry> Entries,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ParsedWorkflowCacheEntry(
        string Yaml,
        WorkflowDefinition? Definition);

    private sealed record WorkflowFileEntry(string Name, string FilePath, string SourceKind);

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
