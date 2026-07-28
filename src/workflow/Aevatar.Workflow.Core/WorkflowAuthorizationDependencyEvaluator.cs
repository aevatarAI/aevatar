using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public static class WorkflowAuthorizationDependencyEvaluator
{
    internal const string DefaultConnectorOperationId = "__default__";

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    };

    public static WorkflowAuthorizationDependencies Evaluate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var externalCapabilities = EnumerateSteps(workflow.Steps)
            .Select(TryEvaluateExternalCapability)
            .Where(static capability => capability is not null)
            .Select(static capability => capability!)
            .ToArray();
        var hasNyxIdCapability = externalCapabilities.Any(static capability =>
            capability.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService);

        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = WorkflowLlmRuntimePolicy.RequiresLlmProvider(workflow),
            ServiceGrantPolicy = hasNyxIdCapability
                ? WorkflowServiceGrantPolicy.Required
                : WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        dependencies.ExternalCapabilities.Add(externalCapabilities);
        return dependencies;
    }

    private static ExternalWorkflowCapabilityRef? TryEvaluateExternalCapability(StepDefinition step)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (string.Equals(canonicalType, "connector_call", StringComparison.OrdinalIgnoreCase))
            return EvaluateConnectorCapability(step);

        if (!string.Equals(canonicalType, "tool_call", StringComparison.OrdinalIgnoreCase) ||
            !step.Parameters.TryGetValue("tool", out var toolName) ||
            !string.Equals(toolName.Trim(), "nyxid_proxy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return EvaluateNyxIdCapability(step);
    }

    private static ExternalWorkflowCapabilityRef EvaluateConnectorCapability(StepDefinition step)
    {
        var connectorRef = ReadStaticStepParameter(step, "connector", required: true);
        var operationId = ReadFirstStaticStepParameter(step, ["operation", "action"])
                          ?? DefaultConnectorOperationId;
        var contractDigest = ReadStaticStepParameter(step, "contract_digest", required: false) ?? string.Empty;

        return new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = connectorRef!,
                OperationId = operationId,
                ContractDigest = contractDigest,
            },
        };
    }

    private static ExternalWorkflowCapabilityRef EvaluateNyxIdCapability(StepDefinition step)
    {
        if (!step.Parameters.TryGetValue("arguments", out var arguments) ||
            string.IsNullOrWhiteSpace(arguments))
        {
            throw Invalid(step, "nyxid_proxy arguments must be a static JSON object.");
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid(step, "nyxid_proxy arguments must be a static JSON object.");
            if (root.TryGetProperty("service", out _))
            {
                throw Invalid(
                    step,
                    "nyxid_proxy service aliases are not identities; exact service_id is required.");
            }

            var serviceId = ReadRequiredStaticString(step, root, "service_id");
            var slug = ReadRequiredStaticString(step, root, "slug");
            var operationId = ReadRequiredStaticString(step, root, "operation_id");
            var method = ReadRequiredStaticString(step, root, "method").ToUpperInvariant();
            var path = ReadRequiredStaticString(step, root, "path");
            var contractDigest = ReadRequiredStaticString(step, root, "contract_digest");
            if (!HttpMethods.Contains(method))
                throw Invalid(step, $"nyxid_proxy method '{method}' is not supported.");

            ValidateHeaders(step, root);
            return new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = serviceId,
                    ServiceSlugSnapshot = slug,
                    OperationId = operationId,
                    HttpMethod = method,
                    PathTemplate = path,
                    ContractDigest = contractDigest,
                },
            };
        }
        catch (WorkflowExternalCapabilityValidationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Invalid(step, "nyxid_proxy arguments must be valid static JSON.");
        }
    }

    private static void ValidateHeaders(StepDefinition step, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("headers", out var headers))
            return;
        if (headers.ValueKind != JsonValueKind.Object)
            throw Invalid(step, "nyxid_proxy headers must be a JSON object.");

        foreach (var header in headers.EnumerateObject())
        {
            if (IsSensitiveHeader(header.Name))
            {
                throw Invalid(
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

    private static string ReadRequiredStaticString(
        StepDefinition step,
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw Invalid(step, $"nyxid_proxy exact {propertyName} is required.");

        var normalized = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || ContainsTemplate(normalized))
            throw Invalid(step, $"nyxid_proxy exact static {propertyName} is required.");
        return normalized;
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

    private static WorkflowExternalCapabilityValidationException Invalid(
        StepDefinition step,
        string detail) =>
        new($"Workflow step '{step.Id}' external capability is invalid: {detail}");

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in EnumerateSteps(step.Children ?? []))
                yield return child;
        }
    }
}
