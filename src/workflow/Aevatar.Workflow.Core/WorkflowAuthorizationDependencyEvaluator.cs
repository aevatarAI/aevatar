using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public static class WorkflowAuthorizationDependencyEvaluator
{
    public static WorkflowAuthorizationDependencies Evaluate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var steps = EnumerateSteps(workflow.Steps).ToArray();
        var connectorRefs = workflow.Roles
            .SelectMany(static role => role.Connectors)
            .Concat(steps
                .Where(static step => string.Equals(
                    WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
                    "connector_call",
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(static step => step.Parameters.TryGetValue("connector", out var connector)
                    ? [connector]
                    : Array.Empty<string>()))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nyxIdServiceSlugs = new List<string>();
        var hasUnresolvedNyxIdProxyDependency = false;
        foreach (var step in steps.Where(static step => string.Equals(
                     WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
                     "tool_call",
                     StringComparison.OrdinalIgnoreCase)))
        {
            if (!step.Parameters.TryGetValue("tool", out var toolName) ||
                !string.Equals(toolName.Trim(), "nyxid_proxy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadStaticNyxIdProxySlug(step, out var serviceSlug))
                nyxIdServiceSlugs.Add(serviceSlug);
            else
                hasUnresolvedNyxIdProxyDependency = true;
        }

        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = WorkflowLlmRuntimePolicy.RequiresLlmProvider(workflow),
            ServiceGrantPolicy = nyxIdServiceSlugs.Count > 0 || hasUnresolvedNyxIdProxyDependency
                ? WorkflowServiceGrantPolicy.Required
                : WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        dependencies.ConnectorCapabilityRefs.Add(connectorRefs);
        dependencies.NyxIdServiceSlugs.Add(nyxIdServiceSlugs);
        return dependencies;
    }

    private static bool TryReadStaticNyxIdProxySlug(StepDefinition step, out string serviceSlug)
    {
        serviceSlug = string.Empty;
        if (!step.Parameters.TryGetValue("arguments", out var arguments) ||
            string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadRuntimeServiceArgument(document.RootElement, out var slugElement))
            {
                return false;
            }

            var candidate = slugElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.Contains("${", StringComparison.Ordinal))
            {
                return false;
            }

            serviceSlug = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRuntimeServiceArgument(JsonElement arguments, out JsonElement value)
    {
        if (arguments.TryGetProperty("slug", out value) && value.ValueKind == JsonValueKind.String)
            return true;
        return arguments.TryGetProperty("service", out value) && value.ValueKind == JsonValueKind.String;
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
}
