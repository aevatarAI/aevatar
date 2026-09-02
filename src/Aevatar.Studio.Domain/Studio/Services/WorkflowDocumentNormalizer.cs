using System.Globalization;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;

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
            AllowedTools = NormalizeAllowedTools(role.AllowedTools),
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
        var normalizedParameters = new StudioStepParameters();

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
                StudioStepParameterValue.FromScalar(step.TimeoutMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        return step with
        {
            Id = step.Id.Trim(),
            Type = canonicalType,
            OriginalType = canonicalType,
            TargetRole = NormalizeText(step.TargetRole),
            UsedRoleAlias = false,
            AllowedTools = NormalizeAllowedTools(step.AllowedTools),
            Capability = NormalizeCapability(step.Capability),
            Parameters = normalizedParameters,
            Next = NormalizeText(step.Next),
            Branches = step.Branches
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.Ordinal),
            Children = step.Children.Select(NormalizeStep).ToList(),
        };
    }

    private static StepCapability? NormalizeCapability(StepCapability? capability)
    {
        if (capability is null)
            return null;

        return capability with
        {
            NyxIdOperation = capability.NyxIdOperation is null
                ? null
                : capability.NyxIdOperation with
                {
                    UserServiceId = capability.NyxIdOperation.UserServiceId.Trim(),
                    EndpointId = capability.NyxIdOperation.EndpointId.Trim(),
                },
            NyxIdRequest = capability.NyxIdRequest is null
                ? null
                : capability.NyxIdRequest with
                {
                    UserServiceId = capability.NyxIdRequest.UserServiceId.Trim(),
                    Method = capability.NyxIdRequest.Method.Trim(),
                    PathTemplate = capability.NyxIdRequest.PathTemplate.Trim(),
                    QueryParameters = capability.NyxIdRequest.QueryParameters
                        .Select(static value => value.Trim())
                        .ToList(),
                    HeaderParameters = capability.NyxIdRequest.HeaderParameters
                        .Select(static value => value.Trim())
                        .ToList(),
                    BodyMode = capability.NyxIdRequest.BodyMode.Trim(),
                    ResponseMode = capability.NyxIdRequest.ResponseMode.Trim(),
                },
        };
    }

    private StudioStepParameterValue? NormalizeParameterValue(string key, StudioStepParameterValue? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.IsComplexValue())
        {
            return value.DeepCloneValue();
        }

        var scalar = value.ToWorkflowScalarString() ?? string.Empty;
        if (_profile.IsStepTypeParameterKey(key))
        {
            scalar = _profile.ToCanonicalType(scalar);
        }

        return StudioStepParameterValue.FromScalar(scalar);
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
                    parameters.TryGetValue("tool", out var toolNode))
                {
                    AddStringIfMissing(parameters, "operation", toolNode?.ToWorkflowScalarString());
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

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string>? NormalizeAllowedTools(IReadOnlyList<string>? allowedTools) =>
        allowedTools is null
            ? null
            : allowedTools
                .Select(NormalizeText)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();

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
