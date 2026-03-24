using System.Globalization;
using System.Text.Json.Nodes;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Utilities;

namespace Aevatar.Studio.Domain.Studio.Services;

public sealed class WorkflowDocumentNormalizer
{
    private readonly WorkflowCompatibilityProfile _profile;

    public WorkflowDocumentNormalizer(WorkflowCompatibilityProfile? profile = null)
    {
        _profile = profile ?? WorkflowCompatibilityProfile.AevatarV1;
    }

    public WorkflowDocument NormalizeForExport(WorkflowDocument document) =>
        document with
        {
            Name = document.Name.Trim(),
            Description = document.Description?.Trim() ?? string.Empty,
            Roles = document.Roles.Select(NormalizeRole).ToList(),
            Steps = document.Steps.Select(NormalizeStep).ToList(),
            Configuration = document.Configuration with { },
        };

    private RoleModel NormalizeRole(RoleModel role)
    {
        var id = role.Id.Trim();
        var name = string.IsNullOrWhiteSpace(role.Name) ? id : role.Name.Trim();

        return role with
        {
            Id = id,
            Name = name,
            SystemPrompt = role.SystemPrompt?.Trim() ?? string.Empty,
            Provider = NormalizeText(role.Provider),
            Model = NormalizeText(role.Model),
            EventModules = NormalizeText(role.EventModules),
            EventRoutes = NormalizeText(role.EventRoutes),
            Connectors = role.Connectors
                .SelectMany(SplitConnectorValue)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private StepModel NormalizeStep(StepModel step)
    {
        var canonicalType = _profile.ToCanonicalType(step.Type);
        var normalizedParameters = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var (key, value) in step.Parameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var normalizedKey = key.Trim();
            var clonedValue = NormalizeParameterValue(normalizedKey, value);
            normalizedParameters[normalizedKey] = clonedValue;
        }

        ApplyErgonomicDefaults(step.OriginalType ?? step.Type, normalizedParameters);

        if (step.TimeoutMs is not null &&
            _profile.ShouldMirrorTimeoutMsToParameters(canonicalType) &&
            !normalizedParameters.ContainsKey("timeout_ms"))
        {
            normalizedParameters["timeout_ms"] =
                JsonValue.Create(step.TimeoutMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        return step with
        {
            Id = step.Id.Trim(),
            Type = canonicalType,
            OriginalType = canonicalType,
            TargetRole = NormalizeText(step.TargetRole),
            UsedRoleAlias = false,
            Parameters = normalizedParameters,
            Next = NormalizeText(step.Next),
            Branches = step.Branches
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.Ordinal),
            Children = step.Children.Select(NormalizeStep).ToList(),
        };
    }

    private JsonNode? NormalizeParameterValue(string key, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.IsComplexValue())
        {
            return value.DeepCloneNode();
        }

        var scalar = value.ToWorkflowScalarString() ?? string.Empty;
        if (_profile.IsStepTypeParameterKey(key))
        {
            scalar = _profile.ToCanonicalType(scalar);
        }

        return JsonValue.Create(scalar);
    }

    private static void ApplyErgonomicDefaults(string rawType, IDictionary<string, JsonNode?> parameters)
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
                    parameters.TryGetValue("tool", out var toolNode))
                {
                    AddStringIfMissing(parameters, "operation", toolNode.ToWorkflowScalarString());
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

    private static void AddStringIfMissing(IDictionary<string, JsonNode?> parameters, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || parameters.ContainsKey(key))
        {
            return;
        }

        parameters[key] = JsonValue.Create(value);
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<string> SplitConnectorValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item));
    }
}
