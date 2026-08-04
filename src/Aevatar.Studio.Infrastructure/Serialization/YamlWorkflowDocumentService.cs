using System.Globalization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;
using WorkflowYamlResourceGuard = Aevatar.Workflow.Core.Primitives.WorkflowYamlResourceGuard;
using WorkflowYamlResourceLimitException = Aevatar.Workflow.Core.Primitives.WorkflowYamlResourceLimitException;

namespace Aevatar.Studio.Infrastructure.Serialization;

public sealed class YamlWorkflowDocumentService : IWorkflowYamlDocumentService
{
    private static readonly HashSet<string> CapabilityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "nyxid_operation",
        "nyxid_request",
    };

    private static readonly HashSet<string> NyxIdOperationFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_service_id",
        "endpoint_id",
    };

    private static readonly HashSet<string> NyxIdRequestFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_service_id",
        "method",
        "path_template",
        "query_parameters",
        "header_parameters",
        "body_required",
        "body_mode",
        "response_mode",
    };

    private readonly WorkflowCompatibilityProfile _profile;
    private readonly ISerializer _serializer;

    public YamlWorkflowDocumentService(WorkflowCompatibilityProfile profile)
    {
        _profile = profile;
        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    public WorkflowParseResult Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new WorkflowParseResult(
                null,
                [ValidationFinding.Error("/", "YAML content is empty.", "Provide a workflow YAML document.")]);
        }

        var findings = new List<ValidationFinding>();
        YamlStream stream;

        try
        {
            WorkflowYamlResourceGuard.Validate(yaml);
            stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (WorkflowYamlResourceLimitException exception)
        {
            return new WorkflowParseResult(
                null,
                [ValidationFinding.Error("/", exception.Message, code: "yaml_resource_limit")]);
        }
        catch (YamlException exception)
        {
            return new WorkflowParseResult(
                null,
                [ValidationFinding.Error("/", $"YAML syntax error: {exception.Message}", code: "yaml_syntax")]);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return new WorkflowParseResult(
                null,
                [ValidationFinding.Error("/", "Workflow YAML must contain a root mapping.", code: "yaml_root_mapping")]);
        }

        ReportUnknownKeys(root, _profile.AllowedRootFields, string.Empty, findings);

        var document = new WorkflowDocument
        {
            Name = ReadScalar(root, "name") ?? string.Empty,
            Description = ReadScalar(root, "description") ?? string.Empty,
            Configuration = ParseConfiguration(root, findings),
            Roles = ParseRoles(root, findings),
            Steps = ParseSteps(root, findings),
        };

        return new WorkflowParseResult(document, findings);
    }

    public string Serialize(WorkflowDocument document)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = document.Name,
        };

        if (!string.IsNullOrWhiteSpace(document.Description))
        {
            root["description"] = document.Description;
        }

        if (document.Configuration.ClosedWorldMode)
        {
            root["configuration"] = new Dictionary<string, object?>
            {
                ["closed_world_mode"] = true,
            };
        }

        if (document.Roles.Count > 0)
        {
            root["roles"] = document.Roles.Select(SerializeRole).ToList();
        }

        root["steps"] = document.Steps.Select(SerializeStep).ToList();
        return _serializer.Serialize(root);
    }

    private WorkflowConfiguration ParseConfiguration(YamlMappingNode root, ICollection<ValidationFinding> findings)
    {
        var configurationNode = GetMapping(root, "configuration");
        if (configurationNode is null)
        {
            return new WorkflowConfiguration();
        }

        ReportUnknownKeys(configurationNode, _profile.AllowedConfigurationFields, "/configuration", findings);
        return new WorkflowConfiguration
        {
            ClosedWorldMode = ReadBoolean(configurationNode, "closed_world_mode", findings, "/configuration") ?? false,
        };
    }

    private List<RoleModel> ParseRoles(YamlMappingNode root, ICollection<ValidationFinding> findings)
    {
        var rolesNode = GetSequence(root, "roles");
        if (rolesNode is null)
        {
            return [];
        }

        var roles = new List<RoleModel>();
        for (var index = 0; index < rolesNode.Children.Count; index++)
        {
            if (rolesNode.Children[index] is not YamlMappingNode roleNode)
            {
                findings.Add(ValidationFinding.Error($"/roles/{index}", "Each role must be a YAML mapping."));
                continue;
            }

            var path = $"/roles/{index}";
            ReportUnknownKeys(roleNode, _profile.AllowedRoleFields, path, findings);

            string? eventModules = ReadScalar(roleNode, "event_modules");
            string? eventRoutes = ReadScalar(roleNode, "event_routes");
            var extensionsNode = GetMapping(roleNode, "extensions");
            if (extensionsNode is not null)
            {
                ReportUnknownKeys(extensionsNode, _profile.AllowedRoleExtensionFields, $"{path}/extensions", findings);
                eventModules ??= ReadScalar(extensionsNode, "event_modules");
                eventRoutes ??= ReadScalar(extensionsNode, "event_routes");
            }

            roles.Add(new RoleModel
            {
                Id = ReadScalar(roleNode, "id") ?? string.Empty,
                Name = ReadScalar(roleNode, "name") ?? ReadScalar(roleNode, "id") ?? string.Empty,
                SystemPrompt = ReadScalar(roleNode, "system_prompt") ?? string.Empty,
                Provider = ReadScalar(roleNode, "provider"),
                Model = ReadScalar(roleNode, "model"),
                Temperature = ReadDouble(roleNode, "temperature", findings, path),
                MaxTokens = ReadInteger(roleNode, "max_tokens", findings, path),
                MaxToolRounds = ReadInteger(roleNode, "max_tool_rounds", findings, path),
                MaxHistoryMessages = ReadInteger(roleNode, "max_history_messages", findings, path),
                AllowedTools = ParseAllowedTools(roleNode, path, findings),
                EventModules = eventModules,
                EventRoutes = eventRoutes,
                Connectors = ParseConnectors(roleNode, path, findings),
            });
        }

        return roles;
    }

    private List<StepModel> ParseSteps(YamlMappingNode root, ICollection<ValidationFinding> findings)
    {
        var stepsNode = GetSequence(root, "steps");
        if (stepsNode is null)
        {
            return [];
        }

        var steps = new List<StepModel>();
        for (var index = 0; index < stepsNode.Children.Count; index++)
        {
            if (stepsNode.Children[index] is not YamlMappingNode stepNode)
            {
                findings.Add(ValidationFinding.Error($"/steps/{index}", "Each step must be a YAML mapping."));
                continue;
            }

            steps.Add(ParseStep(stepNode, $"/steps/{index}", findings));
        }

        return steps;
    }

    private StepModel ParseStep(YamlMappingNode stepNode, string path, ICollection<ValidationFinding> findings)
    {
        ReportUnknownKeys(stepNode, _profile.AllowedStepFields, path, findings);

        var rawType = ReadScalar(stepNode, "type");
        rawType = string.IsNullOrWhiteSpace(rawType) ? "llm_call" : rawType;
        var canonicalType = _profile.ToCanonicalType(rawType);

        var parameters = ParseParameters(stepNode, path, findings);
        ApplyErgonomicDefaults(rawType, parameters);
        CanonicalizeStepTypeParameters(parameters);

        var timeoutMs = ReadInteger(stepNode, "timeout_ms", findings, path);
        if (timeoutMs is not null &&
            _profile.ShouldMirrorTimeoutMsToParameters(canonicalType) &&
            !parameters.ContainsKey("timeout_ms"))
        {
            parameters["timeout_ms"] = StudioStepParameterValue.FromScalar(timeoutMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        return new StepModel
        {
            Id = ReadScalar(stepNode, "id") ?? string.Empty,
            Type = canonicalType,
            OriginalType = rawType,
            TargetRole = ReadScalar(stepNode, "target_role") ?? ReadScalar(stepNode, "role"),
            UsedRoleAlias = GetNode(stepNode, "target_role") is null && GetNode(stepNode, "role") is not null,
            AllowedTools = ParseAllowedTools(stepNode, path, findings),
            Capability = ParseCapability(stepNode, path, findings),
            Parameters = parameters,
            Next = ReadScalar(stepNode, "next"),
            Branches = ParseBranches(stepNode, path, findings),
            Children = ParseChildren(stepNode, path, findings),
            ImportedFromChildren = GetNode(stepNode, "children") is not null,
            Retry = ParseRetry(stepNode, path, findings),
            OnError = ParseOnError(stepNode, path, findings),
            TimeoutMs = timeoutMs,
        };
    }

    private static StepCapability? ParseCapability(
        YamlMappingNode stepNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var raw = GetNode(stepNode, "capability");
        if (raw is null)
            return null;
        if (raw is not YamlMappingNode capabilityNode)
        {
            findings.Add(ValidationFinding.Error(
                $"{path}/capability",
                "`capability` must be a mapping.",
                code: "invalid_field"));
            return null;
        }

        ReportUnknownKeys(capabilityNode, CapabilityFields, $"{path}/capability", findings);
        var operation = ParseNyxIdOperation(capabilityNode, path, findings);
        var request = ParseNyxIdRequest(capabilityNode, path, findings);
        if (operation is not null && request is not null)
        {
            findings.Add(ValidationFinding.Error(
                $"{path}/capability",
                "Select exactly one NyxID capability.",
                code: "invalid_field"));
        }

        return operation is null && request is null
            ? null
            : new StepCapability { NyxIdOperation = operation, NyxIdRequest = request };
    }

    private static NyxIdOperationCapability? ParseNyxIdOperation(
        YamlMappingNode capabilityNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var raw = GetNode(capabilityNode, "nyxid_operation");
        if (raw is null)
            return null;
        if (raw is not YamlMappingNode node)
        {
            findings.Add(ValidationFinding.Error(
                $"{path}/capability/nyxid_operation",
                "`nyxid_operation` must be a mapping.",
                code: "invalid_field"));
            return null;
        }

        ReportUnknownKeys(node, NyxIdOperationFields, $"{path}/capability/nyxid_operation", findings);
        return new NyxIdOperationCapability
        {
            UserServiceId = ReadScalar(node, "user_service_id") ?? string.Empty,
            EndpointId = ReadScalar(node, "endpoint_id") ?? string.Empty,
        };
    }

    private static NyxIdRequestCapability? ParseNyxIdRequest(
        YamlMappingNode capabilityNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var raw = GetNode(capabilityNode, "nyxid_request");
        if (raw is null)
            return null;
        if (raw is not YamlMappingNode node)
        {
            findings.Add(ValidationFinding.Error(
                $"{path}/capability/nyxid_request",
                "`nyxid_request` must be a mapping.",
                code: "invalid_field"));
            return null;
        }

        ReportUnknownKeys(node, NyxIdRequestFields, $"{path}/capability/nyxid_request", findings);
        return new NyxIdRequestCapability
        {
            UserServiceId = ReadScalar(node, "user_service_id") ?? string.Empty,
            Method = ReadScalar(node, "method") ?? string.Empty,
            PathTemplate = ReadScalar(node, "path_template") ?? string.Empty,
            QueryParameters = ReadStringList(node, "query_parameters", path, findings),
            HeaderParameters = ReadStringList(node, "header_parameters", path, findings),
            BodyRequired = ReadBoolean(
                node,
                "body_required",
                findings,
                $"{path}/capability/nyxid_request") ?? false,
            BodyMode = ReadScalar(node, "body_mode") ?? string.Empty,
            ResponseMode = ReadScalar(node, "response_mode") ?? string.Empty,
        };
    }

    private static List<string> ReadStringList(
        YamlMappingNode node,
        string key,
        string stepPath,
        ICollection<ValidationFinding> findings)
    {
        var value = GetNode(node, key);
        if (value is null)
            return [];
        if (value is not YamlSequenceNode sequence ||
            sequence.Children.Any(static child => child is not YamlScalarNode))
        {
            findings.Add(ValidationFinding.Error(
                $"{stepPath}/capability/nyxid_request/{key}",
                $"`{key}` must be a list of strings.",
                code: "invalid_field"));
            return [];
        }

        return sequence.Children.Cast<YamlScalarNode>()
            .Select(static child => child.Value ?? string.Empty)
            .ToList();
    }

    private StudioStepParameters ParseParameters(
        YamlMappingNode stepNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var parameters = new StudioStepParameters();
        var parametersNode = GetMapping(stepNode, "parameters");
        if (parametersNode is not null)
        {
            foreach (var child in parametersNode.Children)
            {
                parameters[ToKey(child.Key)] = ToParameterValue(child.Value);
            }
        }

        foreach (var rootParameterField in _profile.RootParameterFields)
        {
            var fieldNode = GetNode(stepNode, rootParameterField);
            if (fieldNode is null || parameters.ContainsKey(rootParameterField))
            {
                continue;
            }

            parameters[rootParameterField] = ToParameterValue(fieldNode);
        }

        return parameters;
    }

    private List<StepModel> ParseChildren(
        YamlMappingNode stepNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var childrenNode = GetSequence(stepNode, "children");
        if (childrenNode is null)
        {
            return [];
        }

        var children = new List<StepModel>();
        for (var index = 0; index < childrenNode.Children.Count; index++)
        {
            if (childrenNode.Children[index] is not YamlMappingNode childNode)
            {
                findings.Add(ValidationFinding.Error($"{path}/children/{index}", "Each child step must be a YAML mapping."));
                continue;
            }

            children.Add(ParseStep(childNode, $"{path}/children/{index}", findings));
        }

        return children;
    }

    private Dictionary<string, string> ParseBranches(
        YamlMappingNode stepNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var branchesNode = GetNode(stepNode, "branches");
        if (branchesNode is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (branchesNode is YamlMappingNode mappingNode)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in mappingNode.Children)
            {
                var branchKey = ToKey(child.Key);
                var branchValue = ResolveBranchTarget(child.Value);
                if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(branchValue))
                {
                    result[branchKey] = branchValue;
                }
            }

            return result;
        }

        if (branchesNode is YamlSequenceNode sequenceNode)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < sequenceNode.Children.Count; index++)
            {
                if (sequenceNode.Children[index] is not YamlMappingNode branchNode)
                {
                    findings.Add(ValidationFinding.Error($"{path}/branches/{index}", "Each branch entry must be a YAML mapping."));
                    continue;
                }

                ReportUnknownKeys(branchNode, _profile.AllowedBranchListFields, $"{path}/branches/{index}", findings);
                var branchKey = ReadFirstScalar(branchNode, ["condition", "when", "case", "label", "if"]);
                var branchTarget = ReadFirstScalar(branchNode, ["next", "to", "target", "step"]);

                if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(branchTarget))
                {
                    result[branchKey] = branchTarget;
                }
            }

            return result;
        }

        findings.Add(ValidationFinding.Error($"{path}/branches", "`branches` must be a mapping or a list."));
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private StepRetryPolicy? ParseRetry(YamlMappingNode stepNode, string path, ICollection<ValidationFinding> findings)
    {
        var retryNode = GetMapping(stepNode, "retry");
        if (retryNode is null)
        {
            return null;
        }

        ReportUnknownKeys(retryNode, _profile.AllowedRetryFields, $"{path}/retry", findings);
        return new StepRetryPolicy
        {
            MaxAttempts = ReadInteger(retryNode, "max_attempts", findings, $"{path}/retry") ?? 3,
            Backoff = ReadScalar(retryNode, "backoff") ?? "fixed",
            DelayMs = ReadInteger(retryNode, "delay_ms", findings, $"{path}/retry") ?? 1000,
        };
    }

    private StepErrorPolicy? ParseOnError(YamlMappingNode stepNode, string path, ICollection<ValidationFinding> findings)
    {
        var onErrorNode = GetMapping(stepNode, "on_error");
        if (onErrorNode is null)
        {
            return null;
        }

        ReportUnknownKeys(onErrorNode, _profile.AllowedOnErrorFields, $"{path}/on_error", findings);
        return new StepErrorPolicy
        {
            Strategy = ReadScalar(onErrorNode, "strategy") ?? "fail",
            FallbackStep = ReadScalar(onErrorNode, "fallback_step"),
            DefaultOutput = ReadScalar(onErrorNode, "default_output"),
        };
    }

    private List<string> ParseConnectors(
        YamlMappingNode roleNode,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var connectorsNode = GetNode(roleNode, "connectors");
        if (connectorsNode is null)
        {
            return [];
        }

        if (connectorsNode is YamlSequenceNode sequenceNode)
        {
            return sequenceNode.Children
                .Select(node => (node as YamlScalarNode)?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();
        }

        if (connectorsNode is YamlScalarNode scalarNode)
        {
            return (scalarNode.Value ?? string.Empty)
                .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        findings.Add(ValidationFinding.Error($"{path}/connectors", "`connectors` must be a list or delimited string."));
        return [];
    }

    private static List<string>? ParseAllowedTools(
        YamlMappingNode node,
        string path,
        ICollection<ValidationFinding> findings)
    {
        var allowedToolsNode = GetNode(node, "allowed_tools");
        if (allowedToolsNode is null)
        {
            return null;
        }

        if (allowedToolsNode is YamlSequenceNode sequenceNode)
        {
            var tools = new List<string>();
            for (var index = 0; index < sequenceNode.Children.Count; index++)
            {
                if (sequenceNode.Children[index] is not YamlScalarNode scalarNode)
                {
                    findings.Add(ValidationFinding.Error(
                        $"{path}/allowed_tools/{index}",
                        "Each `allowed_tools` entry must be a string."));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(scalarNode.Value))
                {
                    tools.Add(scalarNode.Value.Trim());
                }
            }

            return tools;
        }

        if (allowedToolsNode is YamlScalarNode scalar)
        {
            return (scalar.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        findings.Add(ValidationFinding.Error($"{path}/allowed_tools", "`allowed_tools` must be a list or comma-delimited string."));
        return [];
    }

    private Dictionary<string, object?> SerializeRole(RoleModel role)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: role YAML emitted stream_buffer_capacity as if stream buffering were authoring semantics.
        //   New principle: exported roles include only stable role behavior; ChatRuntime owns stream flow without a YAML buffer knob.
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = role.Id,
        };

        if (!string.IsNullOrWhiteSpace(role.Name))
        {
            result["name"] = role.Name;
        }

        if (!string.IsNullOrWhiteSpace(role.SystemPrompt))
        {
            result["system_prompt"] = role.SystemPrompt;
        }

        AddIfNotNull(result, "provider", role.Provider);
        AddIfNotNull(result, "model", role.Model);
        AddIfNotNull(result, "temperature", role.Temperature);
        AddIfNotNull(result, "max_tokens", role.MaxTokens);
        AddIfNotNull(result, "max_tool_rounds", role.MaxToolRounds);
        AddIfNotNull(result, "max_history_messages", role.MaxHistoryMessages);
        AddIfPresent(result, "allowed_tools", role.AllowedTools);
        AddIfNotNull(result, "event_modules", role.EventModules);
        AddIfNotNull(result, "event_routes", role.EventRoutes);

        if (role.Connectors.Count > 0)
        {
            result["connectors"] = role.Connectors;
        }

        return result;
    }

    private Dictionary<string, object?> SerializeStep(StepModel step)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = step.Id,
            ["type"] = _profile.ToCanonicalType(step.Type),
        };

        if (!string.IsNullOrWhiteSpace(step.TargetRole))
        {
            result["target_role"] = step.TargetRole;
        }

        AddIfPresent(result, "allowed_tools", step.AllowedTools);
        AddIfNotNull(result, "capability", SerializeCapability(step.Capability));

        if (step.Parameters.Count > 0)
        {
            result["parameters"] = step.Parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToPlainValue(),
                StringComparer.Ordinal);
        }

        AddIfNotNull(result, "next", step.Next);

        if (step.Branches.Count > 0)
        {
            result["branches"] = step.Branches.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
        }

        if (step.Children.Count > 0)
        {
            result["children"] = step.Children.Select(SerializeStep).ToList();
        }

        if (step.Retry is not null)
        {
            result["retry"] = new Dictionary<string, object?>
            {
                ["max_attempts"] = step.Retry.MaxAttempts,
                ["backoff"] = step.Retry.Backoff,
                ["delay_ms"] = step.Retry.DelayMs,
            };
        }

        if (step.OnError is not null)
        {
            var onError = new Dictionary<string, object?>
            {
                ["strategy"] = step.OnError.Strategy,
            };

            AddIfNotNull(onError, "fallback_step", step.OnError.FallbackStep);
            AddIfNotNull(onError, "default_output", step.OnError.DefaultOutput);
            result["on_error"] = onError;
        }

        AddIfNotNull(result, "timeout_ms", step.TimeoutMs);
        return result;
    }

    private static object? SerializeCapability(StepCapability? capability)
    {
        if (capability is null)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (capability.NyxIdOperation is not null)
        {
            result["nyxid_operation"] = new Dictionary<string, object?>
            {
                ["user_service_id"] = capability.NyxIdOperation.UserServiceId,
                ["endpoint_id"] = capability.NyxIdOperation.EndpointId,
            };
        }
        if (capability.NyxIdRequest is not null)
        {
            var request = new Dictionary<string, object?>
            {
                ["user_service_id"] = capability.NyxIdRequest.UserServiceId,
                ["method"] = capability.NyxIdRequest.Method,
                ["path_template"] = capability.NyxIdRequest.PathTemplate,
                ["body_required"] = capability.NyxIdRequest.BodyRequired,
                ["body_mode"] = capability.NyxIdRequest.BodyMode,
                ["response_mode"] = capability.NyxIdRequest.ResponseMode,
            };
            AddIfPresent(request, "query_parameters", capability.NyxIdRequest.QueryParameters);
            AddIfPresent(request, "header_parameters", capability.NyxIdRequest.HeaderParameters);
            result["nyxid_request"] = request;
        }

        return result.Count == 0 ? null : result;
    }

    private static void AddIfPresent(IDictionary<string, object?> dictionary, string key, List<string>? value)
    {
        if (value is not null)
        {
            dictionary[key] = value;
        }
    }

    private void CanonicalizeStepTypeParameters(IDictionary<string, StudioStepParameterValue?> parameters)
    {
        foreach (var key in parameters.Keys.ToList())
        {
            if (!_profile.IsStepTypeParameterKey(key))
            {
                continue;
            }

            var value = parameters[key]?.ToWorkflowScalarString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[key] = StudioStepParameterValue.FromScalar(_profile.ToCanonicalType(value));
            }
        }
    }

    private static void ApplyErgonomicDefaults(string rawType, IDictionary<string, StudioStepParameterValue?> parameters)
    {
        var normalized = string.IsNullOrWhiteSpace(rawType)
            ? string.Empty
            : rawType.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "http_get":
                AddStringIfMissing(parameters, "method", "GET");
                break;
            case "http_post":
                AddStringIfMissing(parameters, "method", "POST");
                break;
            case "http_put":
                AddStringIfMissing(parameters, "method", "PUT");
                break;
            case "http_delete":
                AddStringIfMissing(parameters, "method", "DELETE");
                break;
            case "mcp_call":
                if (!parameters.ContainsKey("operation") &&
                    parameters.TryGetValue("tool", out var toolValue))
                {
                    AddStringIfMissing(parameters, "operation", toolValue?.ToWorkflowScalarString());
                }
                break;
            case "foreach_llm":
                AddStringIfMissing(parameters, "sub_step_type", "llm_call");
                break;
            case "map_reduce_llm":
                AddStringIfMissing(parameters, "map_step_type", "llm_call");
                AddStringIfMissing(parameters, "reduce_step_type", "llm_call");
                break;
        }
    }

    private static void AddStringIfMissing(IDictionary<string, StudioStepParameterValue?> parameters, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || parameters.ContainsKey(key))
        {
            return;
        }

        parameters[key] = StudioStepParameterValue.FromScalar(value);
    }

    private static void AddIfNotNull(IDictionary<string, object?> dictionary, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        dictionary[key] = value;
    }

    private static StudioStepParameterValue? ToParameterValue(YamlNode node) =>
        node switch
        {
            YamlScalarNode scalar => ToScalarJsonValue(scalar),
            YamlSequenceNode sequence => StudioStepParameterValue.FromList(sequence.Children.Select(ToParameterValue)),
            YamlMappingNode mapping => StudioStepParameterValue.FromObject(mapping.Children.Select(child =>
                new KeyValuePair<string, StudioStepParameterValue?>(ToKey(child.Key), ToParameterValue(child.Value)))),
            _ => StudioStepParameterValue.FromScalar(node.ToString()),
        };

    private static StudioStepParameterValue? ToScalarJsonValue(YamlScalarNode scalar)
    {
        if (scalar.Tag == "tag:yaml.org,2002:null" ||
            string.IsNullOrWhiteSpace(scalar.Value))
        {
            return string.IsNullOrEmpty(scalar.Value) ? null : StudioStepParameterValue.FromScalar(scalar.Value);
        }

        return StudioStepParameterValue.FromScalar(scalar.Value);
    }

    private static void ReportUnknownKeys(
        YamlMappingNode node,
        IReadOnlySet<string> allowedKeys,
        string path,
        ICollection<ValidationFinding> findings)
    {
        foreach (var child in node.Children)
        {
            var key = ToKey(child.Key);
            if (!allowedKeys.Contains(key))
            {
                var fullPath = string.IsNullOrEmpty(path) ? $"/{key}" : $"{path}/{key}";
                findings.Add(ValidationFinding.Error(fullPath, $"Unknown field '{key}'.", code: "unknown_field"));
            }
        }
    }

    private static YamlNode? GetNode(YamlMappingNode node, string key)
    {
        foreach (var child in node.Children)
        {
            if (string.Equals(ToKey(child.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                return child.Value;
            }
        }

        return null;
    }

    private static YamlMappingNode? GetMapping(YamlMappingNode node, string key) =>
        GetNode(node, key) as YamlMappingNode;

    private static YamlSequenceNode? GetSequence(YamlMappingNode node, string key) =>
        GetNode(node, key) as YamlSequenceNode;

    private static string? ReadScalar(YamlMappingNode node, string key) =>
        GetNode(node, key) is YamlScalarNode scalar ? scalar.Value : null;

    private static string? ReadFirstScalar(YamlMappingNode node, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys)
        {
            var value = ReadScalar(node, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool? ReadBoolean(
        YamlMappingNode node,
        string key,
        ICollection<ValidationFinding> findings,
        string path)
    {
        var rawValue = ReadScalar(node, key);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (bool.TryParse(rawValue, out var parsed))
        {
            return parsed;
        }

        findings.Add(ValidationFinding.Error($"{path}/{key}", $"'{rawValue}' is not a valid boolean."));
        return null;
    }

    private static int? ReadInteger(
        YamlMappingNode node,
        string key,
        ICollection<ValidationFinding> findings,
        string path)
    {
        var rawValue = ReadScalar(node, key);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        findings.Add(ValidationFinding.Error($"{path}/{key}", $"'{rawValue}' is not a valid integer."));
        return null;
    }

    private static double? ReadDouble(
        YamlMappingNode node,
        string key,
        ICollection<ValidationFinding> findings,
        string path)
    {
        var rawValue = ReadScalar(node, key);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        findings.Add(ValidationFinding.Error($"{path}/{key}", $"'{rawValue}' is not a valid number."));
        return null;
    }

    private static string ResolveBranchTarget(YamlNode node)
    {
        if (node is YamlMappingNode mappingNode)
        {
            return ReadFirstScalar(mappingNode, ["next", "to", "target", "step"]) ?? string.Empty;
        }

        return node is YamlScalarNode scalarNode ? scalarNode.Value?.Trim() ?? string.Empty : string.Empty;
    }

    private static string ToKey(YamlNode node) =>
        (node as YamlScalarNode)?.Value?.Trim() ?? string.Empty;
}
