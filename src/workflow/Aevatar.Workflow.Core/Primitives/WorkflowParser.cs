// ─────────────────────────────────────────────────────────────
// WorkflowParser — YAML 工作流解析器
// 将 YAML 文本反序列化为 WorkflowDefinition 及嵌套步骤结构
// ─────────────────────────────────────────────────────────────

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions.Workflows;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Agreement;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// YAML 工作流解析器。使用 snake_case 命名约定将 YAML 解析为强类型工作流定义。
/// </summary>
public sealed class WorkflowParser
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
    private static readonly (string Key, Func<RawStep, object?> Getter)[] RootParameterMappings =
    [
        ("workers", static step => step.Workers),
        ("parallel_count", static step => step.ParallelCount),
        ("count", static step => step.Count),
        ("vote_step_type", static step => step.VoteStepType),
        ("delimiter", static step => step.Delimiter),
        ("sub_step_type", static step => step.SubStepType),
        ("sub_target_role", static step => step.SubTargetRole),
        ("map_step_type", static step => step.MapStepType),
        ("map_target_role", static step => step.MapTargetRole),
        ("reduce_step_type", static step => step.ReduceStepType),
        ("reduce_target_role", static step => step.ReduceTargetRole),
        ("reduce_prompt_prefix", static step => step.ReducePromptPrefix),
        ("signal_name", static step => step.SignalName),
        ("prompt", static step => step.Prompt),
        ("timeout", static step => step.Timeout),
        ("timeout_seconds", static step => step.TimeoutSeconds),
        ("timeout_default_decision", static step => step.TimeoutDefaultDecision),
        ("duration_ms", static step => step.DurationMs),
        ("variable", static step => step.Variable),
        ("on_timeout", static step => step.OnTimeout),
        ("on_reject", static step => step.OnReject),
        ("workflow", static step => step.Workflow),
        ("lifecycle", static step => step.Lifecycle),
        ("query", static step => step.Query),
        ("top_k", static step => step.TopK),
        ("facts", static step => step.Facts),
        ("rule_mode", static step => step.RuleMode),
        ("quorum_count", static step => step.QuorumCount),
        ("quorum_ratio", static step => step.QuorumRatio),
        ("min_approve_count", static step => step.MinApproveCount),
        ("max_approve_count", static step => step.MaxApproveCount),
        ("min_reject_count", static step => step.MinRejectCount),
        ("max_reject_count", static step => step.MaxRejectCount),
        ("min_abstain_count", static step => step.MinAbstainCount),
        ("max_abstain_count", static step => step.MaxAbstainCount),
        ("count_constraints", static step => step.CountConstraints),
        ("label_source", static step => step.LabelSource),
        ("label_field", static step => step.LabelField),
        ("predicate_id", static step => step.PredicateId),
        ("on_agreed", static step => step.OnAgreed),
        ("on_rejected", static step => step.OnRejected),
        ("on_inconclusive", static step => step.OnInconclusive),
        ("winner_policy", static step => step.WinnerPolicy),
        ("action", static step => step.Action),
        ("key", static step => step.Key),
        ("on_conflict", static step => step.OnConflict),
        ("ttl_ms", static step => step.TtlMs),
        ("wait_timeout_ms", static step => step.WaitTimeoutMs),
        ("holder_token", static step => step.HolderToken),
        ("generation", static step => step.Generation),
        ("holder_token_variable", static step => step.HolderTokenVariable),
    ];

    /// <summary>
    /// 解析 YAML 字符串为工作流定义。
    /// </summary>
    /// <param name="yaml">YAML 格式的工作流配置文本。</param>
    /// <returns>解析后的工作流定义。</returns>
    /// <exception cref="InvalidOperationException">YAML 为空或缺少必填字段时抛出。</exception>
    public WorkflowDefinition Parse(string yaml)
    {
        WorkflowYamlResourceGuard.Validate(yaml);
        ValidateRootSchema(yaml);
        var raw = D.Deserialize<Raw>(yaml) ?? throw new InvalidOperationException("YAML 为空");
        return new WorkflowDefinition
        {
            Name = raw.Name ?? throw new InvalidOperationException("缺少 name"),
            Description = raw.Description ?? "",
            WhenToUse = NormalizeText(raw.WhenToUse),
            Roles = (raw.Roles ?? []).Select(MapRole).ToList(),
            Steps = (raw.Steps ?? []).Select(MapStep).ToList(),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = raw.Configuration?.ClosedWorldMode ?? false,
            },
            OnFailure = MapOnFailure(raw.OnFailure),
        };
    }

    private static void ValidateRootSchema(string yaml)
    {
        YamlStream stream = new();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
            return;

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return;

        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
        {
            var rootField = key.Value;
            if (WorkflowYamlRootSchema.IsAcceptedRootField(rootField))
                continue;

            throw new InvalidOperationException(
                $"Unsupported workflow YAML root field '{rootField}'. Allowed root fields: {WorkflowYamlRootSchema.FormatAcceptedRootFields()}.");
        }
    }

    private static RoleDefinition MapRole(RawRole role)
    {
        // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
        //   Old pattern: workflow steps chose actor implementation with raw agent_type/agent_id parameters
        //   New principle: role agent_kind is the stable typed lifecycle input; steps only target roles
        var eventModules = PreferTopLevelText(role.EventModules, role.Extensions?.EventModules);
        var eventRoutes = PreferTopLevelText(role.EventRoutes, role.Extensions?.EventRoutes);

        var roleId = NormalizeText(role.Id);
        var roleName = NormalizeText(role.Name);
        return new RoleDefinition
        {
            Id = roleId ?? throw new InvalidOperationException("role 缺 id"),
            Name = roleName ?? roleId ?? throw new InvalidOperationException("role 缺 name"),
            AgentKind = NormalizeText(role.AgentKind) ?? WorkflowRoleConventions.DefaultAgentKind,
            SystemPrompt = NormalizeText(role.SystemPrompt) ?? string.Empty,
            Provider = NormalizeText(role.Provider),
            Model = NormalizeText(role.Model),
            Temperature = role.Temperature,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            EventModules = eventModules,
            EventRoutes = eventRoutes,
            AgentToolScope = MapAgentToolScope(role.AllowedTools, role.ToolSets),
            Connectors = role.Connectors?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [],
        };
    }

    private static string? PreferTopLevelText(string? topLevel, string? fallback)
    {
        var primary = NormalizeText(topLevel);
        return primary ?? NormalizeText(fallback);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private static StepDefinition MapStep(RawStep s)
    {
        var rawType = s.Type ?? "llm_call";
        var normalizedRawType = rawType.Trim().ToLowerInvariant();
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(rawType);
        var rawParameters = s.Parameters is null
            ? null
            : new Dictionary<string, object?>(s.Parameters, StringComparer.Ordinal);
        var agentToolScope = MapAgentToolScope(
            ResolveStepScopeSource(s.AllowedTools, rawParameters, "allowed_tools", "allowedTools"),
            ResolveStepScopeSource(s.ToolSets, rawParameters, "tool_sets", "toolSets"));
        var presentation = MapPresentation(canonicalType, s, rawParameters);
        var parameters = NormalizeParameters(rawParameters);

        ApplyErgonomicDefaults(normalizedRawType, parameters);
        LiftRootPrimitiveParameters(canonicalType, s, parameters);
        LiftTransformOperationParameters(canonicalType, s, parameters);

        return new StepDefinition
        {
            Id = s.Id ?? throw new InvalidOperationException("step 缺 id"),
            DisplayName = NormalizeText(s.DisplayName),
            Type = canonicalType,
            TargetRole = s.TargetRole ?? s.Role,
            Parameters = WorkflowPrimitiveCatalog.CanonicalizeStepTypeParameters(parameters),
            Capability = MapCapability(s.Capability),
            ResponseProjection = MapResponseProjection(s.ResponseProjection),
            ValueLifecycle = MapValueLifecycle(s.ValueLifecycle),
            TransformOperation = MapTransformOperation(canonicalType, parameters),
            Presentation = presentation,
            AgentToolScope = agentToolScope,
            HumanApprovalOptions = MapHumanApprovalOptions(canonicalType, parameters),
            ExternalApprovalOptions = MapExternalApprovalOptions(canonicalType, parameters),
            ConnectorApprovalOptions = MapConnectorApprovalOptions(canonicalType, parameters),
            Next = s.Next,
            Compensation = NormalizeText(s.Compensation),
            Children = s.Children?.Select(MapStep).ToList(),
            Branches = NormalizeBranches(s.Branches),
            Retry = MapRetry(s.Retry),
            IdempotencyKey = NormalizeText(s.IdempotencyKey),
            OnError = MapOnError(s.OnError),
            TimeoutMs = s.TimeoutMs,
        };
    }

    private static WorkflowStepValueLifecycle? MapValueLifecycle(RawStepValueLifecycle? lifecycle)
    {
        if (lifecycle == null)
            return null;

        var mapped = new WorkflowStepValueLifecycle();
        if (lifecycle.ReleaseVariablesAfterSuccess != null)
            mapped.ReleaseVariablesAfterSuccess.Add(lifecycle.ReleaseVariablesAfterSuccess);
        return mapped;
    }

    private static ExternalWorkflowCapabilitySelector? MapCapability(RawStepCapability? capability)
    {
        if (capability is null)
            return null;
        if (capability.NyxIdOperation is not null && capability.NyxIdRequest is not null)
            throw new InvalidOperationException("Step capability must contain exactly one NyxID capability selector.");

        if (capability.NyxIdOperation is not null)
        {
            return new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = NormalizeText(capability.NyxIdOperation.UserServiceId) ?? string.Empty,
                    EndpointId = NormalizeText(capability.NyxIdOperation.EndpointId) ?? string.Empty,
                },
            };
        }

        if (capability.NyxIdRequest is null)
            return null;

        var request = capability.NyxIdRequest;
        var selector = new NyxIdRequestSelector
        {
            UserServiceId = NormalizeText(request.UserServiceId) ?? string.Empty,
            Method = ParseNyxIdRequestMethod(request.Method),
            PathTemplate = NormalizeText(request.PathTemplate) ?? string.Empty,
            BodyMode = ParseNyxIdRequestBodyMode(request.BodyMode),
            BodyRequired = request.BodyRequired,
            ResponseMode = ParseNyxIdRequestResponseMode(request.ResponseMode),
            Risk = ParseNyxIdOperationRisk(request.Risk),
        };
        selector.QueryParameters.Add(NormalizeNames(request.QueryParameters, StringComparer.Ordinal));
        selector.HeaderParameters.Add(NormalizeNames(request.HeaderParameters, StringComparer.OrdinalIgnoreCase));
        return new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector };
    }

    private static WorkflowToolResponseProjection? MapResponseProjection(
        RawToolResponseProjection? rawProjection)
    {
        if (rawProjection is null)
            return null;

        var projection = new WorkflowToolResponseProjection();
        foreach (var (outputName, rawOperations) in
                 (rawProjection.Fields ?? new Dictionary<string, List<RawToolResponseProjectionOperation>>())
                 .OrderBy(static field => field.Key, StringComparer.Ordinal))
        {
            var field = new WorkflowToolResponseProjectionField
            {
                OutputName = outputName ?? string.Empty,
            };
            foreach (var rawOperation in rawOperations ?? [])
                field.Operations.Add(MapResponseProjectionOperation(rawOperation));
            projection.Fields.Add(field);
        }

        return projection;
    }

    private static WorkflowToolResponseProjectionOperation MapResponseProjectionOperation(
        RawToolResponseProjectionOperation rawOperation)
    {
        ArgumentNullException.ThrowIfNull(rawOperation);
        var populated = (rawOperation.Pointer is null ? 0 : 1) +
                        (rawOperation.ParseJson.HasValue ? 1 : 0) +
                        (rawOperation.ArrayMatch is null ? 0 : 1) +
                        (rawOperation.ArrayMap is null ? 0 : 1);
        if (populated != 1)
        {
            throw new InvalidOperationException(
                "Each response_projection operation must contain exactly one of pointer, parse_json, match, or map.");
        }

        if (rawOperation.Pointer is not null)
            return new WorkflowToolResponseProjectionOperation { JsonPointer = rawOperation.Pointer };
        if (rawOperation.ParseJson.HasValue)
            return new WorkflowToolResponseProjectionOperation { ParseJson = rawOperation.ParseJson.Value };

        if (rawOperation.ArrayMap is not null)
        {
            var arrayMap = new WorkflowToolResponseProjectionArrayMap();
            foreach (var mappedOperation in rawOperation.ArrayMap)
                arrayMap.Operations.Add(MapResponseProjectionMappedOperation(mappedOperation));
            return new WorkflowToolResponseProjectionOperation { ArrayMap = arrayMap };
        }

        return new WorkflowToolResponseProjectionOperation
        {
            ArrayMatch = new WorkflowToolResponseProjectionArrayMatch
            {
                ElementJsonPointer = rawOperation.ArrayMatch!.Pointer ?? string.Empty,
                ExpectedString = rawOperation.ArrayMatch.ExpectedString ?? string.Empty,
            },
        };
    }

    private static WorkflowToolResponseProjectionOperation MapResponseProjectionMappedOperation(
        RawToolResponseProjectionMappedOperation rawOperation)
    {
        ArgumentNullException.ThrowIfNull(rawOperation);
        var populated = (rawOperation.Pointer is null ? 0 : 1) +
                        (rawOperation.ParseJson.HasValue ? 1 : 0) +
                        (rawOperation.ArrayMatch is null ? 0 : 1);
        if (populated != 1)
        {
            throw new InvalidOperationException(
                "Each mapped response_projection operation must contain exactly one of pointer, parse_json, or match.");
        }

        if (rawOperation.Pointer is not null)
            return new WorkflowToolResponseProjectionOperation { JsonPointer = rawOperation.Pointer };
        if (rawOperation.ParseJson.HasValue)
            return new WorkflowToolResponseProjectionOperation { ParseJson = rawOperation.ParseJson.Value };

        return new WorkflowToolResponseProjectionOperation
        {
            ArrayMatch = new WorkflowToolResponseProjectionArrayMatch
            {
                ElementJsonPointer = rawOperation.ArrayMatch!.Pointer ?? string.Empty,
                ExpectedString = rawOperation.ArrayMatch.ExpectedString ?? string.Empty,
            },
        };
    }

    private static IEnumerable<string> NormalizeNames(IEnumerable<string>? values, StringComparer comparer) =>
        (values ?? [])
        .Select(static value => value?.Trim() ?? string.Empty)
        .OrderBy(static value => value, comparer);

    private static NyxIdRequestMethod ParseNyxIdRequestMethod(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "GET" => NyxIdRequestMethod.Get,
            "HEAD" => NyxIdRequestMethod.Head,
            "OPTIONS" => NyxIdRequestMethod.Options,
            "POST" => NyxIdRequestMethod.Post,
            "PUT" => NyxIdRequestMethod.Put,
            "PATCH" => NyxIdRequestMethod.Patch,
            "DELETE" => NyxIdRequestMethod.Delete,
            _ => NyxIdRequestMethod.Unspecified,
        };

    private static NyxIdOperationRisk ParseNyxIdOperationRisk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return NyxIdOperationRisk.Unspecified;

        return value.Trim().ToUpperInvariant() switch
        {
            "READ_ONLY" => NyxIdOperationRisk.ReadOnly,
            "WRITE" => NyxIdOperationRisk.Write,
            "DESTRUCTIVE" => NyxIdOperationRisk.Destructive,
            _ => (NyxIdOperationRisk)(-1),
        };
    }

    private static NyxIdRequestBodyMode ParseNyxIdRequestBodyMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => NyxIdRequestBodyMode.None,
            "json" => NyxIdRequestBodyMode.Json,
            _ => NyxIdRequestBodyMode.Unspecified,
        };

    private static NyxIdRequestResponseMode ParseNyxIdRequestResponseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "text" => NyxIdRequestResponseMode.Text,
            "file_artifact" => NyxIdRequestResponseMode.FileArtifact,
            _ => NyxIdRequestResponseMode.Unspecified,
        };

    private static StepPresentation? MapPresentation(
        string canonicalStepType,
        RawStep step,
        IDictionary<string, object?>? rawParameters)
    {
        var isNotifyStep = string.Equals(canonicalStepType, "notify", StringComparison.Ordinal);
        var interactionSpec = MapInteractionSpec(ResolveInteractionSpecSource(step, rawParameters));
        var interactionTemplateSpec = isNotifyStep
            ? MapInteractionTemplateSpec(ResolveInteractionTemplateSpecSource(step, rawParameters))
            : null;
        var deliveryTargetId = SupportsHumanInteractionDelivery(canonicalStepType)
            ? ResolveDeliveryTargetId(step, rawParameters)
            : null;
        var presentation = new StepPresentation
        {
            InteractionSpec = interactionSpec,
            InteractionTemplateSpec = interactionTemplateSpec,
            DeliveryTargetId = deliveryTargetId,
        };
        return StepPresentation.HasPresentation(presentation)
            ? presentation
            : null;
    }

    private static bool SupportsHumanInteractionDelivery(string canonicalStepType) =>
        string.Equals(canonicalStepType, "notify", StringComparison.Ordinal) ||
        string.Equals(canonicalStepType, "human_approval", StringComparison.Ordinal) ||
        string.Equals(canonicalStepType, "human_input", StringComparison.Ordinal) ||
        string.Equals(canonicalStepType, "secure_input", StringComparison.Ordinal);

    private static object? ResolveInteractionSpecSource(
        RawStep step,
        IDictionary<string, object?>? rawParameters)
    {
        if (step.InteractionSpec is not null)
            return step.InteractionSpec;

        if (step.Presentation?.InteractionSpec is not null)
            return step.Presentation.InteractionSpec;

        if (step.Presentation?.HasInlineSpec() == true)
            return step.Presentation;

        if (rawParameters is null)
            return null;

        foreach (var key in new[] { "interaction_spec", "interactionSpec" })
        {
            if (!rawParameters.TryGetValue(key, out var source))
                continue;

            rawParameters.Remove(key);
            return source;
        }

        return null;
    }

    private static object? ResolveStepScopeSource(
        object? topLevelSource,
        IDictionary<string, object?>? rawParameters,
        params string[] keys)
    {
        object? parameterSource = null;
        if (rawParameters is not null)
        {
            foreach (var key in keys)
            {
                if (!rawParameters.TryGetValue(key, out var source))
                    continue;

                rawParameters.Remove(key);
                parameterSource ??= source;
            }
        }

        return topLevelSource ?? parameterSource;
    }

    private static WorkflowAgentToolScopeDefinition? MapAgentToolScope(
        object? allowedToolsSource,
        object? toolSetsSource)
    {
        if (allowedToolsSource is null && toolSetsSource is null)
            return null;

        return new WorkflowAgentToolScopeDefinition
        {
            RestrictAllowedToolNames = allowedToolsSource is not null,
            RestrictToolSets = toolSetsSource is not null,
            AllowedToolNames = NormalizeToolNames(allowedToolsSource).ToList(),
            ToolSetRefs = NormalizeToolNames(toolSetsSource).ToList(),
        };
    }

    private static IEnumerable<string> NormalizeToolNames(object? source)
    {
        switch (source)
        {
            case null:
                yield break;
            case string text:
            {
                if (string.IsNullOrWhiteSpace(text))
                    yield break;

                var trimmed = text.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) ||
                    trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(trimmed);
                    foreach (var item in NormalizeToolNames(document.RootElement.Clone()))
                        yield return item;
                    yield break;
                }

                foreach (var item in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    yield return item;
                yield break;
            }
            case JsonElement element:
            {
                if (element.ValueKind != JsonValueKind.Array)
                    yield break;

                foreach (var item in element.EnumerateArray())
                {
                    var toolName = ConvertValueToString(item).Trim();
                    if (!string.IsNullOrWhiteSpace(toolName))
                        yield return toolName;
                }

                yield break;
            }
            case IEnumerable sequence:
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in sequence)
                {
                    var toolName = ConvertValueToString(item).Trim();
                    if (!string.IsNullOrWhiteSpace(toolName) && seen.Add(toolName))
                        yield return toolName;
                }

                yield break;
            }
            default:
            {
                var toolName = ConvertValueToString(source).Trim();
                if (!string.IsNullOrWhiteSpace(toolName))
                    yield return toolName;
                yield break;
            }
        }
    }

    private static object? ResolveInteractionTemplateSpecSource(
        RawStep step,
        IDictionary<string, object?>? rawParameters)
    {
        if (step.InteractionTemplateSpec is not null)
            return step.InteractionTemplateSpec;

        if (step.Presentation?.InteractionTemplateSpec is not null)
            return step.Presentation.InteractionTemplateSpec;

        if (rawParameters is null)
            return null;

        foreach (var key in new[] { "interaction_template_spec", "interactionTemplateSpec" })
        {
            if (!rawParameters.TryGetValue(key, out var source))
                continue;

            rawParameters.Remove(key);
            return source;
        }

        return null;
    }

    private static string? ResolveDeliveryTargetId(
        RawStep step,
        IDictionary<string, object?>? rawParameters)
    {
        if (!string.IsNullOrWhiteSpace(step.DeliveryTargetId))
            return step.DeliveryTargetId.Trim();

        if (rawParameters is null)
            return null;

        if (!rawParameters.TryGetValue("delivery_target_id", out var source))
            return null;

        rawParameters.Remove("delivery_target_id");
        var deliveryTargetId = ConvertValueToString(source).Trim();
        return string.IsNullOrWhiteSpace(deliveryTargetId) ? null : deliveryTargetId;
    }

    private static InteractionSpec? MapInteractionSpec(object? source)
    {
        if (source is null)
            return null;

        if (source is InteractionSpec spec)
            return spec.Clone();

        if (source is RawStepPresentation presentation)
            return MapInteractionSpecFromAccessor(
                key => key switch
                {
                    "title" => presentation.Title,
                    "body" => presentation.Body,
                    "actions" => presentation.Actions,
                    "fields" => presentation.Fields,
                    "cards" => presentation.Cards,
                    "disposition" => presentation.Disposition,
                    _ => null,
                });

        if (source is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using var document = JsonDocument.Parse(text);
            return MapInteractionSpec(document.RootElement);
        }

        if (source is JsonElement element)
            return MapInteractionSpecFromJson(element);

        if (source is IDictionary mapping)
            return MapInteractionSpecFromAccessor(key => TryReadMappingObject(mapping, KeyCandidates(key)));

        return null;
    }

    private static InteractionSpec? MapInteractionSpecFromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return MapInteractionSpecFromAccessor(key => TryReadJsonProperty(element, KeyCandidates(key)));
    }

    private static InteractionSpec? MapInteractionSpecFromAccessor(Func<string, object?> read)
    {
        var spec = new InteractionSpec
        {
            Title = ConvertValueToString(read("title")).Trim(),
            Body = ConvertValueToString(read("body")).Trim(),
            Disposition = ParseDisposition(ConvertValueToString(read("disposition"))),
        };

        foreach (var action in MapActions(read("actions")))
            spec.Actions.Add(action);
        foreach (var field in MapFields(read("fields")))
            spec.Fields.Add(field);
        foreach (var card in MapCards(read("cards")))
            spec.Cards.Add(card);

        return StepPresentation.HasInteractionSpec(spec) ? spec : null;
    }

    private static InteractionTemplateSpec? MapInteractionTemplateSpec(object? source)
    {
        if (source is null)
            return null;

        if (source is InteractionTemplateSpec spec)
            return spec.Clone();

        if (source is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using var document = JsonDocument.Parse(text);
            return MapInteractionTemplateSpec(document.RootElement);
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            return MapInteractionTemplateSpecFromAccessor(
                key => TryReadJsonProperty(element, KeyCandidates(key)));
        }

        if (source is IDictionary mapping)
            return MapInteractionTemplateSpecFromAccessor(
                key => TryReadMappingObject(mapping, KeyCandidates(key)));

        return null;
    }

    private static InteractionTemplateSpec? MapInteractionTemplateSpecFromAccessor(Func<string, object?> read)
    {
        var spec = new InteractionTemplateSpec
        {
            TemplateId = ConvertValueToString(read("template_id")).Trim(),
        };

        foreach (var (key, value) in MapStringMap(read("template_variable")))
            spec.TemplateVariable[key] = value;

        return StepPresentation.HasInteractionTemplateSpec(spec) ? spec : null;
    }

    private static IEnumerable<KeyValuePair<string, string>> MapStringMap(object? source)
    {
        if (source is null)
            yield break;

        if (source is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            using var document = JsonDocument.Parse(text);
            foreach (var item in MapStringMap(document.RootElement.Clone()))
                yield return item;
            yield break;
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var property in element.EnumerateObject())
            {
                var key = property.Name.Trim();
                var value = ConvertValueToString(property.Value).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    yield return new KeyValuePair<string, string>(key, value);
            }

            yield break;
        }

        if (source is IDictionary mapping)
        {
            foreach (DictionaryEntry entry in mapping)
            {
                var key = ConvertValueToString(entry.Key).Trim();
                var value = ConvertValueToString(entry.Value).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }

    private static IEnumerable<InteractionAction> MapActions(object? source)
    {
        foreach (var item in EnumerateItems(source))
        {
            var action = MapAction(item);
            if (action is not null)
                yield return action;
        }
    }

    private static InteractionAction? MapAction(object? source)
    {
        var action = MapFromObject(
            source,
            read =>
            {
                var mapped = new InteractionAction
                {
                    ActionId = ConvertValueToString(read("action_id")).Trim(),
                    Label = ConvertValueToString(read("label")).Trim(),
                    Value = ConvertValueToString(read("value")).Trim(),
                    Placeholder = ConvertValueToString(read("placeholder")).Trim(),
                    Kind = ParseActionKind(ConvertValueToString(read("kind"))),
                    Style = ParseActionStyle(ConvertValueToString(read("style"))),
                    ApprovalDecision = ParseApprovalDecision(ConvertValueToString(read("approval_decision"))),
                    Disabled = ParseBool(read("disabled")) || ParseBool(read("is_disabled")),
                };

                foreach (var option in MapOptions(read("options")))
                    mapped.Options.Add(option);

                if (mapped.Kind == InteractionActionKind.Unspecified &&
                    (!string.IsNullOrWhiteSpace(mapped.ActionId) ||
                     !string.IsNullOrWhiteSpace(mapped.Label)))
                {
                    mapped.Kind = mapped.Options.Count > 0
                        ? InteractionActionKind.Select
                        : InteractionActionKind.Button;
                }

                return mapped;
            });

        return action is not null &&
               (!string.IsNullOrWhiteSpace(action.ActionId) ||
                !string.IsNullOrWhiteSpace(action.Label) ||
                !string.IsNullOrWhiteSpace(action.Value) ||
                action.Options.Count > 0)
            ? action
            : null;
    }

    private static IEnumerable<InteractionOption> MapOptions(object? source)
    {
        foreach (var item in EnumerateItems(source))
        {
            var option = MapFromObject(
                item,
                read => new InteractionOption
                {
                    Label = ConvertValueToString(read("label")).Trim(),
                    Value = ConvertValueToString(read("value")).Trim(),
                });

            if (option is not null &&
                (!string.IsNullOrWhiteSpace(option.Label) ||
                 !string.IsNullOrWhiteSpace(option.Value)))
            {
                yield return option;
            }
        }
    }

    private static IEnumerable<InteractionField> MapFields(object? source)
    {
        foreach (var item in EnumerateItems(source))
        {
            var field = MapField(item);
            if (field is not null)
                yield return field;
        }
    }

    private static InteractionField? MapField(object? source)
    {
        var field = MapFromObject(
            source,
            read => new InteractionField
            {
                Title = ConvertValueToString(read("title")).Trim(),
                Text = ConvertValueToString(read("text")).Trim(),
                IsShort = ParseBool(read("is_short")),
            });

        return field is not null &&
               (!string.IsNullOrWhiteSpace(field.Title) ||
                !string.IsNullOrWhiteSpace(field.Text))
            ? field
            : null;
    }

    private static IEnumerable<InteractionCard> MapCards(object? source)
    {
        foreach (var item in EnumerateItems(source))
        {
            var card = MapCard(item);
            if (card is not null)
                yield return card;
        }
    }

    private static InteractionCard? MapCard(object? source)
    {
        var card = MapFromObject(
            source,
            read =>
            {
                var mapped = new InteractionCard
                {
                    BlockId = ConvertValueToString(read("block_id")).Trim(),
                    Title = ConvertValueToString(read("title")).Trim(),
                    Text = ConvertValueToString(read("text")).Trim(),
                    ImageUrl = ConvertValueToString(read("image_url")).Trim(),
                    Kind = ParseCardKind(ConvertValueToString(read("kind"))),
                };

                foreach (var field in MapFields(read("fields")))
                    mapped.Fields.Add(field);
                foreach (var action in MapActions(read("actions")))
                    mapped.Actions.Add(action);

                if (mapped.Kind == InteractionCardKind.Unspecified &&
                    (!string.IsNullOrWhiteSpace(mapped.Title) ||
                     !string.IsNullOrWhiteSpace(mapped.Text) ||
                     mapped.Fields.Count > 0 ||
                     mapped.Actions.Count > 0))
                {
                    mapped.Kind = InteractionCardKind.Section;
                }

                return mapped;
            });

        return card is not null &&
               (!string.IsNullOrWhiteSpace(card.BlockId) ||
                !string.IsNullOrWhiteSpace(card.Title) ||
                !string.IsNullOrWhiteSpace(card.Text) ||
                !string.IsNullOrWhiteSpace(card.ImageUrl) ||
                card.Fields.Count > 0 ||
                card.Actions.Count > 0)
            ? card
            : null;
    }

    private static T? MapFromObject<T>(object? source, Func<Func<string, object?>, T> map)
        where T : class
    {
        if (source is null)
            return null;

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            return map(key => TryReadJsonProperty(element, KeyCandidates(key)));
        }

        if (source is IDictionary mapping)
            return map(key => TryReadMappingObject(mapping, KeyCandidates(key)));

        if (source is string text && !string.IsNullOrWhiteSpace(text))
        {
            using var document = JsonDocument.Parse(text);
            return MapFromObject(document.RootElement.Clone(), map);
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateItems(object? source)
    {
        if (source is null)
            yield break;

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var item in element.EnumerateArray())
                yield return item.Clone();
            yield break;
        }

        if (source is string text && !string.IsNullOrWhiteSpace(text))
        {
            using var document = JsonDocument.Parse(text);
            foreach (var item in EnumerateItems(document.RootElement.Clone()))
                yield return item;
            yield break;
        }

        if (source is IEnumerable sequence && source is not string)
        {
            foreach (var item in sequence)
                yield return item;
        }
    }

    private static object? TryReadMappingObject(IDictionary mapping, params string[] candidates)
    {
        foreach (DictionaryEntry entry in mapping)
        {
            var key = ConvertValueToString(entry.Key);
            if (candidates.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                return entry.Value;
        }

        return null;
    }

    private static object? TryReadJsonProperty(JsonElement element, params string[] candidates)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (candidates.Any(candidate => string.Equals(candidate, property.Name, StringComparison.OrdinalIgnoreCase)))
                return property.Value.Clone();
        }

        return null;
    }

    private static string[] KeyCandidates(string key) =>
        key switch
        {
            "action_id" => ["action_id", "actionId", "id"],
            "block_id" => ["block_id", "blockId", "id"],
            "image_url" => ["image_url", "imageUrl"],
            "is_short" => ["is_short", "isShort", "short"],
            "is_disabled" => ["is_disabled", "isDisabled"],
            "approval_decision" => ["approval_decision", "approvalDecision"],
            "template_id" => ["template_id", "templateId"],
            "template_variable" => ["template_variable", "templateVariable", "template_variables", "templateVariables", "variables"],
            _ => [key],
        };

    private static InteractionDisposition ParseDisposition(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "normal" => InteractionDisposition.Normal,
            "ephemeral" => InteractionDisposition.Ephemeral,
            "silent" => InteractionDisposition.Silent,
            "pinned" => InteractionDisposition.Pinned,
            _ => InteractionDisposition.Unspecified,
        };

    private static InteractionActionKind ParseActionKind(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "button" => InteractionActionKind.Button,
            "select" => InteractionActionKind.Select,
            "formsubmit" => InteractionActionKind.FormSubmit,
            "link" => InteractionActionKind.Link,
            "textinput" => InteractionActionKind.TextInput,
            _ => InteractionActionKind.Unspecified,
        };

    private static InteractionActionStyle ParseActionStyle(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "default" => InteractionActionStyle.Default,
            "primary" => InteractionActionStyle.Primary,
            "danger" => InteractionActionStyle.Danger,
            _ => InteractionActionStyle.Unspecified,
        };

    private static InteractionApprovalDecision ParseApprovalDecision(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "approve" or "approved" => InteractionApprovalDecision.Approve,
            "reject" or "rejected" => InteractionApprovalDecision.Reject,
            _ => InteractionApprovalDecision.Unspecified,
        };

    private static InteractionCardKind ParseCardKind(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "section" => InteractionCardKind.Section,
            "actions" => InteractionCardKind.Actions,
            "image" => InteractionCardKind.Image,
            "context" => InteractionCardKind.Context,
            "divider" => InteractionCardKind.Divider,
            _ => InteractionCardKind.Unspecified,
        };

    private static string NormalizeEnumToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static bool ParseBool(object? value) =>
        value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => bool.TryParse(ConvertValueToString(value), out var parsed) && parsed,
        };

    private static void ApplyErgonomicDefaults(string normalizedRawType, IDictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(normalizedRawType))
            return;

        switch (normalizedRawType)
        {
            case "http_get":
                AddIfMissing(parameters, "method", "GET");
                break;
            case "http_post":
                AddIfMissing(parameters, "method", "POST");
                break;
            case "http_put":
                AddIfMissing(parameters, "method", "PUT");
                break;
            case "http_delete":
                AddIfMissing(parameters, "method", "DELETE");
                break;
            case "mcp_call":
                // Accept mcp_call + tool, then forward as connector_call.operation for consistent metadata.
                if (!parameters.ContainsKey("operation") &&
                    !parameters.ContainsKey("action") &&
                    parameters.TryGetValue("tool", out var toolName) &&
                    !string.IsNullOrWhiteSpace(toolName))
                {
                    parameters["operation"] = toolName;
                }
                break;
            case "foreach_llm":
                AddIfMissing(parameters, "sub_step_type", "llm_call");
                break;
            case "map_reduce_llm":
                AddIfMissing(parameters, "map_step_type", "llm_call");
                AddIfMissing(parameters, "reduce_step_type", "llm_call");
                break;
        }
    }

    private static Dictionary<string, string> NormalizeParameters(
        IReadOnlyDictionary<string, object?>? rawParameters)
    {
        if (rawParameters == null || rawParameters.Count == 0)
            return [];

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rawParameters)
            normalized[key] = ConvertValueToString(value);
        return normalized;
    }

    private static void LiftRootPrimitiveParameters(
        string canonicalType,
        RawStep s,
        IDictionary<string, string> parameters)
    {
        // Common LLM output pattern: puts primitive params at step root instead of parameters.
        foreach (var (key, getter) in RootParameterMappings)
        {
            if (VoteAgreementRuleConfigurationParser.IsRuleParameterKey(key))
            {
                if (canonicalType == "parallel")
                    AddIfMissing(parameters, $"vote_param_{key}", getter(s));
                else if (canonicalType == "vote")
                    AddIfMissing(parameters, key, getter(s));

                continue;
            }

            AddIfMissing(parameters, key, getter(s));
        }

        // Root timeout_ms may be either primitive parameter or step timeout.
        // Keep step timeout via StepDefinition.TimeoutMs, and also mirror to parameters
        // for primitives that naturally consume timeout_ms.
        if (!parameters.ContainsKey("timeout_ms") &&
            s.TimeoutMs is not null &&
            ShouldLiftTimeoutMsToParameter(canonicalType))
        {
            parameters["timeout_ms"] = ConvertValueToString(s.TimeoutMs.Value);
        }
    }

    private static bool ShouldLiftTimeoutMsToParameter(string canonicalType) =>
        canonicalType is "wait_signal" or "connector_call" or "secure_connector_call" or "llm_call" or "human_input" or "secure_input" or "human_approval";

    private static void LiftTransformOperationParameters(
        string canonicalType,
        RawStep s,
        IDictionary<string, string> parameters)
    {
        if (!string.Equals(canonicalType, "transform", StringComparison.Ordinal))
            return;

        AddIfMissing(parameters, "op", s.Op);
        AddIfMissing(parameters, "precision", s.Precision);
        AddIfMissing(parameters, "group_by", s.GroupBy);
        AddIfMissing(parameters, "value", s.Value);
        AddIfMissing(parameters, "value_field", s.ValueField);
        AddIfMissing(parameters, "field", s.Field);
        AddIfMissing(parameters, "aggregate", s.Aggregate);
        AddIfMissing(parameters, "template", s.Template);
    }

    private static TransformOperationSpec? MapTransformOperation(
        string canonicalType,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.Equals(canonicalType, "transform", StringComparison.Ordinal))
            return null;

        var op = GetParameter(parameters, "op", "operation").Trim();
        var kind = ParseTransformOperationKind(op);
        if (kind == TransformOperationKind.Unspecified)
            return null;

        var spec = new TransformOperationSpec { Kind = kind };
        if (TryGetParameter(parameters, out var precision, "precision", "scale") &&
            !string.IsNullOrWhiteSpace(precision) &&
            !int.TryParse(precision.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        if (TryGetParameter(parameters, out precision, "precision", "scale") &&
            int.TryParse(precision.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPrecision))
        {
            spec.Precision = parsedPrecision;
        }

        spec.Key = GetParameter(parameters, "key", "group_key", "group_by").Trim();
        spec.Value = GetParameter(parameters, "value", "value_field", "field").Trim();
        spec.Aggregate = ParseTransformAggregateKind(GetParameter(parameters, "aggregate", "agg").Trim());
        spec.Template = GetParameter(parameters, "template");
        return spec;
    }

    private static HumanApprovalOptionsDefinition? MapHumanApprovalOptions(
        string canonicalType,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.Equals(canonicalType, "human_approval", StringComparison.Ordinal))
            return null;

        var decision = GetParameter(parameters, "timeout_default_decision").Trim();
        return string.IsNullOrWhiteSpace(decision)
            ? null
            : new HumanApprovalOptionsDefinition { TimeoutDefaultDecision = decision };
    }

    private static ExternalApprovalWaitOptionsDefinition? MapExternalApprovalOptions(
        string canonicalType,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.Equals(canonicalType, "wait_signal", StringComparison.Ordinal))
            return null;

        var sourceId = GetParameter(parameters, "external_approval.source_id", "approval_source_id", "source_id").Trim();
        var externalIdKind = GetParameter(parameters, "external_approval.external_id_kind", "external_id_kind").Trim();
        var externalId = GetParameter(parameters, "external_approval.external_id", "external_id").Trim();
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(externalIdKind) ||
            string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        return new ExternalApprovalWaitOptionsDefinition
        {
            SourceId = sourceId,
            ExternalIdKind = externalIdKind,
            ExternalId = externalId,
            SignalName = GetParameter(parameters, "external_approval.signal_name", "signal_name", "signal").Trim(),
            CallbackIdempotencyKey = GetParameter(
                parameters,
                "external_approval.callback_idempotency_key",
                "callback_idempotency_key",
                "idempotency_key").Trim(),
            RequestId = GetParameter(parameters, "external_approval.request_id", "request_id").Trim(),
        };
    }

    private static ConnectorApprovalOptionsDefinition? MapConnectorApprovalOptions(
        string canonicalType,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (canonicalType is not ("connector_call" or "secure_connector_call"))
            return null;

        var policy = NormalizeEnumToken(GetParameter(
            parameters,
            "approval.policy",
            "approval_policy",
            "approval_required"));
        if (policy is not ("required" or "always" or "true"))
            return null;

        var expirationSeconds = ParseNonNegativeInt(GetParameter(
            parameters,
            "approval.expiration_seconds",
            "approval_expiration_seconds"));
        var statusCheckIntervalSeconds = ParseNonNegativeInt(GetParameter(
            parameters,
            "approval.status_check_interval_seconds",
            "approval_status_check_interval_seconds"));

        return new ConnectorApprovalOptionsDefinition
        {
            ServiceRef = GetParameter(parameters, "approval.service_ref", "approval_service_ref").Trim(),
            NodeId = GetParameter(parameters, "approval.node_id", "approval_node_id").Trim(),
            HttpVerb = GetParameter(parameters, "approval.http_verb", "approval_http_verb").Trim(),
            Resource = GetParameter(parameters, "approval.resource", "approval_resource").Trim(),
            PermissionScope = GetParameter(parameters, "approval.permission_scope", "approval_permission_scope").Trim(),
            ExpirationSeconds = expirationSeconds,
            StatusCheckIntervalSeconds = statusCheckIntervalSeconds == 0
                ? ConnectorApprovalOptionsDefinition.DefaultStatusCheckIntervalSeconds
                : statusCheckIntervalSeconds,
            Destructive = ParseBool(GetParameter(parameters, "approval.destructive", "approval_destructive")),
            TeamId = GetParameter(parameters, "approval.team_id", "approval_team_id").Trim(),
            MemberId = GetParameter(parameters, "approval.member_id", "approval_member_id").Trim(),
            WorkflowId = GetParameter(parameters, "approval.workflow_id", "approval_workflow_id").Trim(),
            PublishedServiceId = GetParameter(
                parameters,
                "approval.published_service_id",
                "approval_published_service_id").Trim(),
            PolicyReason = GetParameter(parameters, "approval.policy_reason", "approval_policy_reason").Trim(),
        };
    }

    private static int ParseNonNegativeInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;

    private static TransformOperationKind ParseTransformOperationKind(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "sum" => TransformOperationKind.Sum,
            "subtract" => TransformOperationKind.Subtract,
            "multiply" => TransformOperationKind.Multiply,
            "divide" => TransformOperationKind.Divide,
            "round" => TransformOperationKind.Round,
            "min" => TransformOperationKind.Min,
            "max" => TransformOperationKind.Max,
            "groupby" => TransformOperationKind.GroupBy,
            "template" => TransformOperationKind.Template,
            _ => TransformOperationKind.Unspecified,
        };

    private static TransformAggregateKind ParseTransformAggregateKind(string? value) =>
        NormalizeEnumToken(value) switch
        {
            "sum" => TransformAggregateKind.Sum,
            "count" => TransformAggregateKind.Count,
            "avg" => TransformAggregateKind.Avg,
            _ => TransformAggregateKind.Unspecified,
        };

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, string> parameters,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out value!))
                return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetParameter(
        IReadOnlyDictionary<string, string> parameters,
        params string[] keys) =>
        TryGetParameter(parameters, out var value, keys) ? value : string.Empty;

    private static void AddIfMissing(
        IDictionary<string, string> parameters,
        string key,
        object? value)
    {
        if (parameters.ContainsKey(key) || value == null)
            return;

        var serialized = ConvertValueToString(value);
        if (string.IsNullOrWhiteSpace(serialized))
            return;

        parameters[key] = serialized;
    }

    private static Dictionary<string, string>? NormalizeBranches(object? rawBranches)
    {
        if (rawBranches == null)
            return null;

        if (rawBranches is IDictionary mapping)
            return NormalizeDictBranches(mapping);

        if (rawBranches is IEnumerable sequence && rawBranches is not string)
            return NormalizeListBranches(sequence);

        return null;
    }

    private static Dictionary<string, string>? NormalizeDictBranches(IDictionary mapping)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in mapping)
        {
            var branchKey = ConvertValueToString(entry.Key);
            var target = ResolveBranchTarget(entry.Value);
            if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(target))
                normalized[branchKey] = target;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, string>? NormalizeListBranches(IEnumerable sequence)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        // Accept list-style branches:
        // branches:
        //   - condition: "true"
        //     next: done
        foreach (var item in sequence)
        {
            if (item is not IDictionary branchItem)
                continue;

            var branchKey = TryReadMappingValue(branchItem, "condition", "when", "case", "label", "if");
            var target = TryReadMappingValue(branchItem, "next", "to", "target", "step");
            if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(target))
                normalized[branchKey] = target;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static string ResolveBranchTarget(object? rawValue)
    {
        if (rawValue is IDictionary mapping)
        {
            var target = TryReadMappingValue(mapping, "next", "to", "target", "step");
            if (!string.IsNullOrWhiteSpace(target))
                return target;
        }

        return ConvertValueToString(rawValue);
    }

    private static string? TryReadMappingValue(IDictionary mapping, params string[] candidates)
    {
        foreach (DictionaryEntry entry in mapping)
        {
            var key = ConvertValueToString(entry.Key);
            if (!candidates.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                continue;

            var value = ResolveBranchTarget(entry.Value);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ConvertValueToString(object? value)
    {
        if (value == null)
            return string.Empty;

        if (value is string text)
            return text;

        if (value is bool flag)
            return flag ? "true" : "false";

        if (value is IDictionary || value is IEnumerable and not string)
            return JsonSerializer.Serialize(NormalizeYamlValue(value));

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object? NormalizeYamlValue(object? value)
    {
        if (value == null)
            return null;

        if (value is IDictionary mapping)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in mapping)
                dict[ConvertValueToString(entry.Key)] = NormalizeYamlValue(entry.Value);
            return dict;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
                list.Add(NormalizeYamlValue(item));
            return list;
        }

        return value;
    }

    private static StepRetryPolicy? MapRetry(RawRetry? r) =>
        r == null ? null : new StepRetryPolicy
        {
            MaxAttempts = r.MaxAttempts ?? 3,
            Backoff = r.Backoff ?? "fixed",
            DelayMs = r.DelayMs ?? 1000,
        };

    private static StepErrorPolicy? MapOnError(RawOnError? e) =>
        e == null ? null : new StepErrorPolicy
        {
            Strategy = e.Strategy ?? "fail",
            FallbackStep = string.IsNullOrWhiteSpace(e.FallbackStep) ? e.Fallback : e.FallbackStep,
            DefaultOutput = e.DefaultOutput,
        };

    private static WorkflowRunFailurePolicy? MapOnFailure(RawOnFailure? policy)
    {
        if (policy == null)
            return null;

        var action = NormalizeText(policy.Action);
        if (action == null)
            return null;

        return new WorkflowRunFailurePolicy
        {
            Action = action,
            MaxAttempts = policy.MaxAttempts ?? 0,
        };
    }

    private sealed class Raw { public string? Name { get; set; } public string? Description { get; set; } public string? WhenToUse { get; set; } public List<RawRole>? Roles { get; set; } public List<RawStep>? Steps { get; set; } public RawConfiguration? Configuration { get; set; } public RawOnFailure? OnFailure { get; set; } }
    private sealed class RawRole
    {
        // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
        //   Old pattern: raw YAML exposed step-level CLR lifecycle selectors
        //   New principle: raw YAML accepts role-level agent_kind and maps it to RoleDefinition.AgentKind
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? AgentKind { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public int? MaxToolRounds { get; set; }
        public int? MaxHistoryMessages { get; set; }
        public string? EventModules { get; set; }
        public string? EventRoutes { get; set; }
        public object? AllowedTools { get; set; }
        public object? ToolSets { get; set; }
        public RawRoleExtensions? Extensions { get; set; }
        public List<string>? Connectors { get; set; }
    }
    private sealed class RawStep
    {
        public string? Id { get; set; }
        [YamlMember(Alias = "display_name")]
        public string? DisplayName { get; set; }
        public string? Type { get; set; }
        public string? TargetRole { get; set; }
        public string? Role { get; set; }
        public object? Workers { get; set; }
        public object? ParallelCount { get; set; }
        public object? Count { get; set; }
        public string? VoteStepType { get; set; }
        public object? Delimiter { get; set; }
        public string? SubStepType { get; set; }
        public string? SubTargetRole { get; set; }
        public string? MapStepType { get; set; }
        public string? MapTargetRole { get; set; }
        public string? ReduceStepType { get; set; }
        public string? ReduceTargetRole { get; set; }
        public string? ReducePromptPrefix { get; set; }
        public string? SignalName { get; set; }
        public string? Prompt { get; set; }
        public object? Timeout { get; set; }
        public object? TimeoutSeconds { get; set; }
        public string? TimeoutDefaultDecision { get; set; }
        public object? DurationMs { get; set; }
        public string? Variable { get; set; }
        public string? OnTimeout { get; set; }
        public string? OnReject { get; set; }
        public string? Workflow { get; set; }
        public string? Lifecycle { get; set; }
        public string? Query { get; set; }
        public object? TopK { get; set; }
        public object? Facts { get; set; }
        public string? RuleMode { get; set; }
        public object? QuorumCount { get; set; }
        public object? QuorumRatio { get; set; }
        public object? MinApproveCount { get; set; }
        public object? MaxApproveCount { get; set; }
        public object? MinRejectCount { get; set; }
        public object? MaxRejectCount { get; set; }
        public object? MinAbstainCount { get; set; }
        public object? MaxAbstainCount { get; set; }
        public object? CountConstraints { get; set; }
        public string? LabelSource { get; set; }
        public string? LabelField { get; set; }
        public string? PredicateId { get; set; }
        public string? OnAgreed { get; set; }
        public string? OnRejected { get; set; }
        public string? OnInconclusive { get; set; }
        public string? WinnerPolicy { get; set; }
        public string? Action { get; set; }
        public string? Key { get; set; }
        public string? OnConflict { get; set; }
        public object? TtlMs { get; set; }
        public object? WaitTimeoutMs { get; set; }
        public string? HolderToken { get; set; }
        public object? Generation { get; set; }
        public string? HolderTokenVariable { get; set; }
        public string? Op { get; set; }
        public object? Precision { get; set; }
        public string? GroupBy { get; set; }
        public string? Value { get; set; }
        public string? ValueField { get; set; }
        public string? Field { get; set; }
        public string? Aggregate { get; set; }
        public string? Template { get; set; }
        public object? AllowedTools { get; set; }
        public object? ToolSets { get; set; }
        public RawStepCapability? Capability { get; set; }
        [YamlMember(Alias = "response_projection")]
        public RawToolResponseProjection? ResponseProjection { get; set; }
        [YamlMember(Alias = "value_lifecycle")]
        public RawStepValueLifecycle? ValueLifecycle { get; set; }
        public object? InteractionSpec { get; set; }
        public object? InteractionTemplateSpec { get; set; }
        public string? DeliveryTargetId { get; set; }
        public RawStepPresentation? Presentation { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
        public string? Next { get; set; }
        public string? Compensation { get; set; }
        public List<RawStep>? Children { get; set; }
        public object? Branches { get; set; }
        public RawRetry? Retry { get; set; }
        public string? IdempotencyKey { get; set; }
        public RawOnError? OnError { get; set; }
        public int? TimeoutMs { get; set; }
    }

    private sealed class RawStepValueLifecycle
    {
        [YamlMember(Alias = "release_variables_after_success")]
        public List<string>? ReleaseVariablesAfterSuccess { get; set; }
    }

    private sealed class RawStepCapability
    {
        [YamlMember(Alias = "nyxid_operation")]
        public RawNyxIdOperationSelector? NyxIdOperation { get; set; }

        [YamlMember(Alias = "nyxid_request")]
        public RawNyxIdRequestSelector? NyxIdRequest { get; set; }
    }

    private sealed class RawToolResponseProjection
    {
        public Dictionary<string, List<RawToolResponseProjectionOperation>>? Fields { get; set; }
    }

    private sealed class RawToolResponseProjectionOperation
    {
        public string? Pointer { get; set; }

        [YamlMember(Alias = "parse_json")]
        public bool? ParseJson { get; set; }

        [YamlMember(Alias = "match")]
        public RawToolResponseProjectionArrayMatch? ArrayMatch { get; set; }

        [YamlMember(Alias = "map")]
        public List<RawToolResponseProjectionMappedOperation>? ArrayMap { get; set; }
    }

    private sealed class RawToolResponseProjectionMappedOperation
    {
        public string? Pointer { get; set; }

        [YamlMember(Alias = "parse_json")]
        public bool? ParseJson { get; set; }

        [YamlMember(Alias = "match")]
        public RawToolResponseProjectionArrayMatch? ArrayMatch { get; set; }
    }

    private sealed class RawToolResponseProjectionArrayMatch
    {
        public string? Pointer { get; set; }

        [YamlMember(Alias = "equals")]
        public string? ExpectedString { get; set; }
    }

    private sealed class RawNyxIdOperationSelector
    {
        public string? UserServiceId { get; set; }
        public string? EndpointId { get; set; }
    }

    private sealed class RawNyxIdRequestSelector
    {
        public string? UserServiceId { get; set; }
        public string? Method { get; set; }
        public string? PathTemplate { get; set; }
        public List<string>? QueryParameters { get; set; }
        public List<string>? HeaderParameters { get; set; }
        public string? BodyMode { get; set; }
        public bool BodyRequired { get; set; }
        public string? ResponseMode { get; set; }
        public string? Risk { get; set; }
    }
    private sealed class RawStepPresentation
    {
        public object? InteractionSpec { get; set; }
        public object? InteractionTemplateSpec { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public object? Actions { get; set; }
        public object? Fields { get; set; }
        public object? Cards { get; set; }
        public string? Disposition { get; set; }

        public bool HasInlineSpec() =>
            !string.IsNullOrWhiteSpace(Title) ||
            !string.IsNullOrWhiteSpace(Body) ||
            Actions is not null ||
            Fields is not null ||
            Cards is not null ||
            !string.IsNullOrWhiteSpace(Disposition);
    }
    private sealed class RawRetry { public int? MaxAttempts { get; set; } public string? Backoff { get; set; } public int? DelayMs { get; set; } }
    private sealed class RawOnError
    {
        public string? Strategy { get; set; }
        public string? FallbackStep { get; set; }
        // Backward-compatible alias for LLM-generated YAML that used on_error.fallback.
        public string? Fallback { get; set; }
        public string? DefaultOutput { get; set; }
    }
    private sealed class RawConfiguration { public bool? ClosedWorldMode { get; set; } }
    private sealed class RawOnFailure { public string? Action { get; set; } public int? MaxAttempts { get; set; } }
    private sealed class RawRoleExtensions { public string? EventModules { get; set; } public string? EventRoutes { get; set; } }
}
