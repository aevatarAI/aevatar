using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public static class WorkflowAuthorizationDependencyEvaluator
{
    internal const string DefaultConnectorOperationId = "__default__";

    /// <summary>The workflow tool whose every call site must carry a committed admission proof.</summary>
    public const string NyxIdProxyToolName = "nyxid_proxy";

    private static readonly HashSet<string> NyxIdRuntimeArgumentNames = new(StringComparer.Ordinal)
    {
        "path_params",
        "query",
        "headers",
        "body",
        "response_mode",
    };

    private static readonly HashSet<string> NyxIdServerDerivedArgumentNames = new(StringComparer.Ordinal)
    {
        "service",
        "service_id",
        "user_service_id",
        "slug",
        "service_slug_snapshot",
        "operation_id",
        "endpoint_id",
        "method",
        "http_method",
        "path",
        "path_template",
        "schema",
        "contract_digest",
        "source_stamp",
    };

    /// <summary>True when a dispatched tool may only run against a committed call-site proof.</summary>
    public static bool RequiresExternalCapabilityAdmission(string? toolName) =>
        string.Equals(toolName?.Trim(), NyxIdProxyToolName, StringComparison.OrdinalIgnoreCase);

    public static WorkflowAuthorizationDependencies Evaluate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var invocations = EnumerateInvocationSteps(workflow)
            .Select(TryCompileExternalInvocation)
            .Where(static invocation => invocation is not null)
            .Select(static invocation => invocation!)
            .OrderBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueCallSites(invocations);

        var hasNyxIdInvocation = invocations.Any(static invocation =>
            RequiresExternalCapabilityAdmission(invocation.ToolName));
        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = WorkflowLlmRuntimePolicy.RequiresLlmProvider(workflow),
            ServiceGrantPolicy = hasNyxIdInvocation
                ? WorkflowServiceGrantPolicy.Required
                : WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        dependencies.ExternalInvocations.Add(invocations.Select(static invocation => invocation.Clone()));
        return dependencies;
    }

    /// <summary>
    /// Compiles the external invocation for a step dispatched directly by the execution kernel.
    /// Runtime and admission must derive the same call-site identity, so both go through here.
    /// </summary>
    public static ExternalToolInvocationSpec? TryCompileDirectInvocation(
        string workflowName,
        StepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return TryCompileExternalInvocation(
            new InvocationStep(step, BuildCallSiteId(workflowName, step.Id), IsSynthesized: false));
    }

    /// <summary>
    /// Compiles the external invocation for the sub-step a looping primitive synthesizes at runtime.
    /// The call-site identity is the owner step's, never the dynamic per-item or per-iteration id.
    /// </summary>
    public static ExternalToolInvocationSpec? TryCompileSynthesizedSubStepInvocation(
        string workflowName,
        StepDefinition ownerStep)
    {
        ArgumentNullException.ThrowIfNull(ownerStep);
        var synthesized = TryBuildSynthesizedSubStep(ownerStep);
        return synthesized is null
            ? null
            : TryCompileExternalInvocation(new InvocationStep(
                synthesized,
                BuildSynthesizedCallSiteId(workflowName, ownerStep.Id),
                IsSynthesized: true));
    }

    private static ExternalToolInvocationSpec? TryCompileExternalInvocation(InvocationStep invocation)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(invocation.Step.Type);
        if (string.Equals(canonicalType, "connector_call", StringComparison.OrdinalIgnoreCase))
            return CompileConnectorInvocation(invocation);

        if (!string.Equals(canonicalType, "tool_call", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!invocation.Step.Parameters.TryGetValue("tool", out var rawToolName) ||
            string.IsNullOrWhiteSpace(rawToolName))
        {
            if (invocation.IsSynthesized)
                throw Invalid(invocation.Step, "indirect tool_call requires one static tool name.");
            return null;
        }

        var toolName = rawToolName.Trim();
        if (ContainsTemplate(toolName))
            throw Invalid(invocation.Step, "tool name must be static.");
        if (IsDirectNyxIdConnectedServiceTool(toolName))
        {
            throw MigrationInvalid(
                invocation.Step,
                "direct NyxID connected-service tool names are no longer supported; select an operation through nyxid_proxy and rebind.");
        }

        if (!RequiresExternalCapabilityAdmission(toolName))
        {
            if (invocation.Step.Capability is not null)
                throw Invalid(invocation.Step, "step capability is only valid for its matching external tool invocation.");
            return null;
        }

        ValidateNyxIdRuntimeArguments(invocation.Step);
        var selector = invocation.Step.Capability?.Clone() ?? new ExternalWorkflowCapabilitySelector();
        if (selector.SelectorCase is not (
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.None or
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation or
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest))
        {
            throw SelectionInvalid(invocation.Step, "nyxid_proxy requires a NyxID operation selector.");
        }

        if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation)
            ValidateNyxIdSelector(invocation.Step, selector.NyxIdOperation);
        else if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest)
            ValidateNyxIdRequestSelector(invocation.Step, selector.NyxIdRequest);

        return new ExternalToolInvocationSpec
        {
            CallSiteId = invocation.CallSiteId,
            ToolName = NyxIdProxyToolName,
            Selector = selector,
        };
    }

    private static ExternalToolInvocationSpec CompileConnectorInvocation(InvocationStep invocation)
    {
        var connectorRef = ReadStaticStepParameter(invocation.Step, "connector", required: true);
        var operationId = ReadFirstStaticStepParameter(invocation.Step, ["operation", "action"])
                          ?? DefaultConnectorOperationId;
        var contractDigest = ReadStaticStepParameter(invocation.Step, "contract_digest", required: false)
                             ?? string.Empty;
        return new ExternalToolInvocationSpec
        {
            CallSiteId = invocation.CallSiteId,
            ToolName = "connector_call",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                HostConnector = new HostConnectorCapabilityRef
                {
                    ConnectorCapabilityRef = connectorRef!,
                    OperationId = operationId,
                    ContractDigest = contractDigest,
                },
            },
        };
    }

    private static void ValidateNyxIdSelector(StepDefinition step, NyxIdOperationSelector selector)
    {
        if (string.IsNullOrWhiteSpace(selector.UserServiceId) ||
            string.IsNullOrWhiteSpace(selector.EndpointId))
        {
            throw SelectionInvalid(step, "NyxID capability must select an exact connected service and operation.");
        }

        if (ContainsTemplate(selector.UserServiceId) || ContainsTemplate(selector.EndpointId))
            throw SelectionInvalid(step, "NyxID service and operation selectors must be static.");
    }

    private static void ValidateNyxIdRequestSelector(StepDefinition step, NyxIdRequestSelector selector)
    {
        if (!NyxIdRequestSelectorContract.TryNormalize(selector, out _, out var error))
            throw SelectionInvalid(step, $"NyxID {error}.");
    }

    private static void ValidateNyxIdRuntimeArguments(StepDefinition step)
    {
        if (!step.Parameters.TryGetValue("arguments", out var arguments) ||
            string.IsNullOrWhiteSpace(arguments))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw ArgumentInvalid(step, "nyxid_proxy arguments must be a JSON object.");

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (NyxIdServerDerivedArgumentNames.Contains(property.Name))
                {
                    throw MigrationInvalid(
                        step,
                        $"nyxid_proxy derived field '{property.Name}' cannot be authored; select a connected-service operation and rebind.");
                }

                if (!NyxIdRuntimeArgumentNames.Contains(property.Name))
                    throw ArgumentInvalid(step, $"nyxid_proxy runtime argument '{property.Name}' is not supported.");
            }

            ValidateObjectSlot(step, document.RootElement, "path_params");
            ValidateObjectSlot(step, document.RootElement, "query");
            ValidateHeaders(step, document.RootElement);
            if (document.RootElement.TryGetProperty("response_mode", out var responseMode) &&
                responseMode.ValueKind != JsonValueKind.String)
            {
                throw ArgumentInvalid(step, "nyxid_proxy response_mode must be a string.");
            }
        }
        catch (JsonException)
        {
            throw ArgumentInvalid(step, "nyxid_proxy arguments must be valid JSON.");
        }
    }

    private static void ValidateObjectSlot(StepDefinition step, JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Object)
        {
            throw ArgumentInvalid(step, $"nyxid_proxy {propertyName} must be a JSON object.");
        }
    }

    private static void ValidateHeaders(StepDefinition step, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("headers", out var headers))
            return;
        if (headers.ValueKind != JsonValueKind.Object)
            throw ArgumentInvalid(step, "nyxid_proxy headers must be a JSON object.");

        foreach (var header in headers.EnumerateObject())
        {
            if (IsSensitiveHeader(header.Name))
            {
                throw ArgumentInvalid(
                    step,
                    $"nyxid_proxy sensitive header '{header.Name}' cannot be supplied by Workflow YAML.");
            }
        }
    }

    internal static bool IsSensitiveHeader(string headerName)
    {
        var normalized = new string((headerName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "authorization" or "proxyauthorization" or "cookie" or "setcookie" or
               "apikey" or "xapikey" or "token" or "apitoken" or "xauthtoken" or
               "accesstoken" or "xaccesstoken" or "bearertoken" ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("accesstoken", StringComparison.Ordinal) ||
               normalized.EndsWith("authtoken", StringComparison.Ordinal);
    }

    private static IEnumerable<InvocationStep> EnumerateInvocationSteps(WorkflowDefinition workflow)
    {
        foreach (var step in EnumerateSteps(workflow.Steps))
        {
            yield return new InvocationStep(
                step,
                BuildCallSiteId(workflow.Name, step.Id),
                IsSynthesized: false);

            var synthesized = TryBuildSynthesizedSubStep(step);
            if (synthesized is not null)
            {
                yield return new InvocationStep(
                    synthesized,
                    BuildSynthesizedCallSiteId(workflow.Name, step.Id),
                    IsSynthesized: true);
            }
        }
    }

    private static StepDefinition? TryBuildSynthesizedSubStep(StepDefinition owner)
    {
        var ownerType = WorkflowPrimitiveCatalog.ToCanonicalType(owner.Type);
        if (ownerType is not ("foreach" or "while"))
            return null;

        var subStepTypeKey = ownerType == "foreach" ? "sub_step_type" : "step";
        var defaultSubStepType = ownerType == "foreach" ? "parallel" : "llm_call";
        var subStepType = owner.Parameters.GetValueOrDefault(subStepTypeKey, defaultSubStepType);
        var canonicalSubStepType = WorkflowPrimitiveCatalog.ToCanonicalType(subStepType);
        var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in owner.Parameters)
        {
            if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                subParameters[key["sub_param_".Length..]] = value;
        }

        return new StepDefinition
        {
            Id = $"{owner.Id}/sub-step",
            Type = canonicalSubStepType,
            TargetRole = owner.Parameters.GetValueOrDefault("sub_target_role"),
            Parameters = subParameters,
            Capability = owner.Capability?.Clone(),
        };
    }

    private static string BuildSynthesizedCallSiteId(string workflowName, string ownerStepId) =>
        $"{BuildCallSiteId(workflowName, ownerStepId)}/sub-step";

    private static string BuildCallSiteId(string workflowName, string stepId)
    {
        var normalizedWorkflow = workflowName?.Trim() ?? string.Empty;
        var normalizedStep = stepId?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalizedWorkflow)
            ? normalizedStep
            : $"{normalizedWorkflow}/{normalizedStep}";
    }

    private static void EnsureUniqueCallSites(IEnumerable<ExternalToolInvocationSpec> invocations)
    {
        var duplicate = invocations
            .GroupBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new WorkflowExternalCapabilityValidationException(
                $"Workflow external capability call site '{duplicate.Key}' is duplicated.");
    }

    private static string? ReadFirstStaticStepParameter(StepDefinition step, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var value = ReadStaticStepParameter(step, name, required: false);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static string? ReadStaticStepParameter(StepDefinition step, string name, bool required)
    {
        if (!step.Parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw Invalid(step, $"{name} is required for connector_call.");
            return null;
        }

        var normalized = value.Trim();
        if (ContainsTemplate(normalized))
            throw Invalid(step, $"connector_call {name} must be static.");
        return normalized;
    }

    private static bool ContainsTemplate(string value) =>
        value.Contains("${", StringComparison.Ordinal);

    private static bool IsDirectNyxIdConnectedServiceTool(string toolName) =>
        toolName.StartsWith("nyxid_", StringComparison.OrdinalIgnoreCase) &&
        toolName.Contains("__", StringComparison.Ordinal);

    private static WorkflowExternalCapabilityValidationException Invalid(
        StepDefinition step,
        string detail) =>
        TypedInvalid(
            step,
            detail,
            ExternalCapabilityReadinessStatus.ContractDrift,
            "WORKFLOW_EXTERNAL_CAPABILITY_DECLARATION_INVALID",
            "The workflow external capability declaration is invalid.",
            ExternalCapabilityRemediationActionKind.RebindWorkflow);

    private static WorkflowExternalCapabilityValidationException SelectionInvalid(
        StepDefinition step,
        string detail) =>
        TypedInvalid(
            step,
            detail,
            ExternalCapabilityReadinessStatus.OperationSelectionRequired,
            "NYXID_OPERATION_SELECTION_REQUIRED",
            "Select an exact connected service operation.",
            ExternalCapabilityRemediationActionKind.SelectOperation);

    private static WorkflowExternalCapabilityValidationException MigrationInvalid(
        StepDefinition step,
        string detail) =>
        TypedInvalid(
            step,
            detail,
            ExternalCapabilityReadinessStatus.ContractDrift,
            "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
            "The workflow contains legacy NyxID operation proof fields.",
            ExternalCapabilityRemediationActionKind.RebindWorkflow);

    private static WorkflowExternalCapabilityValidationException ArgumentInvalid(
        StepDefinition step,
        string detail) =>
        TypedInvalid(
            step,
            detail,
            ExternalCapabilityReadinessStatus.ContractDrift,
            "NYXID_OPERATION_ARGUMENT_INVALID",
            "The NyxID operation runtime arguments are invalid.",
            ExternalCapabilityRemediationActionKind.RebindWorkflow);

    private static WorkflowExternalCapabilityValidationException TypedInvalid(
        StepDefinition step,
        string detail,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage,
        ExternalCapabilityRemediationActionKind remediationKind)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            Status = status,
            SelectedSelector = SafeSelector(step.Capability),
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = status,
            Code = code,
            SafeMessage = safeMessage,
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = remediationKind,
            Label = remediationKind == ExternalCapabilityRemediationActionKind.SelectOperation
                ? "Select operation"
                : "Update and rebind workflow",
        });
        return new WorkflowExternalCapabilityValidationException(
            $"Workflow step '{step.Id}' external capability is invalid: {detail}",
            readiness);
    }

    private static ExternalWorkflowCapabilitySelector? SafeSelector(
        ExternalWorkflowCapabilitySelector? selector)
    {
        if (selector?.SelectorCase is not (
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation or
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest))
        {
            return null;
        }

        if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest)
            return string.IsNullOrWhiteSpace(selector.NyxIdRequest.UserServiceId)
                ? null
                : selector.Clone();

        var userServiceId = selector.NyxIdOperation.UserServiceId;
        var endpointId = selector.NyxIdOperation.EndpointId;
        return string.IsNullOrWhiteSpace(userServiceId) ||
               string.IsNullOrWhiteSpace(endpointId) ||
               ContainsTemplate(userServiceId) ||
               ContainsTemplate(endpointId)
            ? null
            : selector.Clone();
    }

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in EnumerateSteps(step.Children ?? []))
                yield return child;
        }
    }

    private sealed record InvocationStep(
        StepDefinition Step,
        string CallSiteId,
        bool IsSynthesized);
}
