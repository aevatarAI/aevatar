using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Utilities;

namespace Aevatar.Studio.Domain.Studio.Services;

public sealed record WorkflowValidationOptions
{
    public static WorkflowValidationOptions Default { get; } = new();

    // Compatibility-only field; Studio validation no longer blocks step types by closed world mode.
    public bool? ForceClosedWorldMode { get; init; }

    public IReadOnlySet<string>? AvailableWorkflowNames { get; init; }
}

public sealed class WorkflowValidator
{
    private readonly WorkflowCompatibilityProfile _profile;

    public WorkflowValidator(WorkflowCompatibilityProfile? profile = null)
    {
        _profile = profile ?? WorkflowCompatibilityProfile.AevatarV1;
    }

    public IReadOnlyList<ValidationFinding> Validate(
        WorkflowDocument document,
        WorkflowValidationOptions? options = null)
    {
        options ??= WorkflowValidationOptions.Default;
        var findings = new List<ValidationFinding>();

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            findings.Add(ValidationFinding.Error("/name", "Workflow name is required.", "Provide a non-empty `name`."));
        }

        if (document.Steps.Count == 0)
        {
            findings.Add(ValidationFinding.Error("/steps", "At least one workflow step is required.", "Add one step to the workflow."));
            return findings;
        }

        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Roles.Count; index++)
        {
            var role = document.Roles[index];
            var path = $"/roles/{index}";

            if (string.IsNullOrWhiteSpace(role.Id))
            {
                findings.Add(ValidationFinding.Error($"{path}/id", "Role id is required."));
                continue;
            }

            if (!roleIds.Add(role.Id))
            {
                findings.Add(ValidationFinding.Error($"{path}/id", $"Duplicate role id '{role.Id}'."));
            }
        }

        var stepVisits = EnumerateSteps(document.Steps, "/steps").ToList();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var visit in stepVisits)
        {
            if (string.IsNullOrWhiteSpace(visit.Step.Id))
            {
                findings.Add(ValidationFinding.Error($"{visit.Path}/id", "Step id is required."));
                continue;
            }

            if (!stepIds.Add(visit.Step.Id))
            {
                findings.Add(ValidationFinding.Error($"{visit.Path}/id", $"Duplicate step id '{visit.Step.Id}'."));
            }
        }

        foreach (var visit in stepVisits)
        {
            ValidateStep(visit, roleIds, stepIds, options.AvailableWorkflowNames, findings);
        }

        return findings;
    }

    private void ValidateStep(
        StepVisit visit,
        IReadOnlySet<string> roleIds,
        IReadOnlySet<string> stepIds,
        IReadOnlySet<string>? availableWorkflowNames,
        ICollection<ValidationFinding> findings)
    {
        var step = visit.Step;
        var stepPath = visit.Path;
        var canonicalType = _profile.ToCanonicalType(step.Type);

        if (!_profile.IsKnownStepType(canonicalType))
        {
            findings.Add(ValidationFinding.Error(
                $"{stepPath}/type",
                $"Unknown step type '{step.Type}'.",
                "Use a canonical primitive from the compatibility profile.",
                "unknown_step_type"));
        }
        else if (_profile.IsForbiddenAuthoringType(canonicalType))
        {
            findings.Add(ValidationFinding.Error(
                $"{stepPath}/type",
                $"'{canonicalType}' is an internal primitive and cannot be authored in Studio.",
                code: "forbidden_step_type"));
        }
        else if (_profile.IsAdvancedImportOnly(canonicalType))
        {
            findings.Add(ValidationFinding.Warning(
                $"{stepPath}/type",
                $"'{canonicalType}' is import-compatible but not recommended for regular authoring.",
                "Keep it only when round-tripping existing runtime YAML.",
                "import_only_step_type"));
        }

        if (!string.IsNullOrWhiteSpace(step.OriginalType) &&
            !string.Equals(step.OriginalType, canonicalType, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(ValidationFinding.Warning(
                $"{stepPath}/type",
                $"Alias '{step.OriginalType}' will be normalized to canonical type '{canonicalType}' on save.",
                code: "alias_normalized"));
        }

        if (step.UsedRoleAlias)
        {
            findings.Add(ValidationFinding.Warning(
                stepPath,
                $"Step '{step.Id}' uses `role`; Studio will export `target_role`.",
                code: "role_alias"));
        }

        if (step.Children.Count > 0 || step.ImportedFromChildren)
        {
            findings.Add(ValidationFinding.Warning(
                $"{stepPath}/children",
                "`children` is preserved for import compatibility, but it is not the primary v1 authoring model.",
                code: "children_import_only"));
        }

        if (step.Parameters.Any(parameter => parameter.Value.IsComplexValue()))
        {
            findings.Add(ValidationFinding.Warning(
                $"{stepPath}/parameters",
                "Nested object or array parameters are preserved, but runtime execution ultimately consumes parameter dictionaries.",
                code: "complex_parameter"));
        }

        if (!string.IsNullOrWhiteSpace(step.TargetRole) && !roleIds.Contains(step.TargetRole))
        {
            findings.Add(ValidationFinding.Error(
                $"{stepPath}/target_role",
                $"Target role '{step.TargetRole}' does not exist.",
                code: "missing_role"));
        }

        if (!string.IsNullOrWhiteSpace(step.Next) && !stepIds.Contains(step.Next))
        {
            findings.Add(ValidationFinding.Error(
                $"{stepPath}/next",
                $"Next step '{step.Next}' does not exist.",
                code: "missing_next"));
        }

        foreach (var branch in step.Branches)
        {
            var branchPath = $"{stepPath}/branches/{branch.Key}";
            if (string.IsNullOrWhiteSpace(branch.Value))
            {
                findings.Add(ValidationFinding.Error(branchPath, $"Branch '{branch.Key}' is missing a target step."));
            }
            else if (!stepIds.Contains(branch.Value))
            {
                findings.Add(ValidationFinding.Error(branchPath, $"Branch '{branch.Key}' points to missing step '{branch.Value}'."));
            }
        }

        ValidateStepTypeParameters(step, stepPath, findings);
        ValidateTypeSpecificRules(step, stepPath, stepIds, availableWorkflowNames, findings);

        if (visit.Index < visit.SiblingCount - 1 && string.IsNullOrWhiteSpace(step.Next) && step.Branches.Count == 0)
        {
            findings.Add(ValidationFinding.Warning(
                stepPath,
                $"Step '{step.Id}' relies on implicit sequential ordering because `next` is not set.",
                code: "implicit_next"));
        }
    }

    private void ValidateStepTypeParameters(StepModel step, string stepPath, ICollection<ValidationFinding> findings)
    {
        foreach (var (key, value) in step.Parameters)
        {
            if (!_profile.IsStepTypeParameterKey(key))
            {
                continue;
            }

            var parameterValue = value.ToWorkflowScalarString();
            if (string.IsNullOrWhiteSpace(parameterValue))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/parameters/{key}",
                    $"Step type parameter '{key}' cannot be empty.",
                    code: "empty_step_type_parameter"));
                continue;
            }

            var canonical = _profile.ToCanonicalType(parameterValue);
            if (!_profile.IsKnownStepType(canonical))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/parameters/{key}",
                    $"Unknown step type '{parameterValue}' used in parameter '{key}'.",
                    code: "unknown_parameter_step_type"));
            }
        }
    }

    private void ValidateTypeSpecificRules(
        StepModel step,
        string stepPath,
        IReadOnlySet<string> stepIds,
        IReadOnlySet<string>? availableWorkflowNames,
        ICollection<ValidationFinding> findings)
    {
        var stepType = _profile.ToCanonicalType(step.Type);

        if (stepType == "conditional")
        {
            if (!step.Branches.ContainsKey("true"))
            {
                findings.Add(ValidationFinding.Error($"{stepPath}/branches", "`conditional` requires a `true` branch."));
            }

            if (!step.Branches.ContainsKey("false"))
            {
                findings.Add(ValidationFinding.Error($"{stepPath}/branches", "`conditional` requires a `false` branch."));
            }

            return;
        }

        if (stepType == "switch")
        {
            if (!step.Branches.ContainsKey("_default"))
            {
                findings.Add(ValidationFinding.Error($"{stepPath}/branches", "`switch` requires an `_default` branch."));
            }

            return;
        }

        if (stepType == "while")
        {
            var hasCondition = HasNonEmptyParameter(step, "condition");
            var hasMaxIterations = HasPositiveIntegerParameter(step, "max_iterations");

            if (!hasCondition && !hasMaxIterations)
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/parameters",
                    "`while` requires either `condition` or a positive `max_iterations`."));
            }

            if (step.Parameters.TryGetValue("max_iterations", out var maxIterationsNode) &&
                !string.IsNullOrWhiteSpace(maxIterationsNode.ToWorkflowScalarString()) &&
                !HasPositiveIntegerParameter(step, "max_iterations"))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/parameters/max_iterations",
                    "`max_iterations` must be a positive integer."));
            }

            return;
        }

        if (stepType == "workflow_call")
        {
            var workflowName = GetParameter(step, "workflow");
            if (string.IsNullOrWhiteSpace(workflowName))
            {
                findings.Add(ValidationFinding.Error($"{stepPath}/parameters/workflow", "`workflow_call` requires `workflow`."));
            }

            var lifecycle = GetParameter(step, "lifecycle");
            if (!_profile.IsSupportedWorkflowCallLifecycle(lifecycle))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/parameters/lifecycle",
                    "`workflow_call.lifecycle` only supports singleton, transient, or scope."));
            }

            if (!string.IsNullOrWhiteSpace(workflowName) &&
                availableWorkflowNames is not null &&
                !availableWorkflowNames.Contains(workflowName))
            {
                findings.Add(ValidationFinding.Warning(
                    $"{stepPath}/parameters/workflow",
                    $"Referenced child workflow '{workflowName}' was not found in the current bundle.",
                    code: "missing_bundle_workflow"));
            }

            return;
        }

        if (step.OnError is { Strategy: "fallback" } onError)
        {
            if (string.IsNullOrWhiteSpace(onError.FallbackStep))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/on_error/fallback_step",
                    "`fallback` strategy requires `fallback_step`."));
            }
            else if (!stepIds.Contains(onError.FallbackStep))
            {
                findings.Add(ValidationFinding.Error(
                    $"{stepPath}/on_error/fallback_step",
                    $"Fallback step '{onError.FallbackStep}' does not exist."));
            }
        }
    }

    private static string? GetParameter(StepModel step, string key) =>
        step.Parameters.TryGetValue(key, out var value) ? value.ToWorkflowScalarString() : null;

    private static bool HasNonEmptyParameter(StepModel step, string key) =>
        !string.IsNullOrWhiteSpace(GetParameter(step, key));

    private static bool HasPositiveIntegerParameter(StepModel step, string key)
    {
        var value = GetParameter(step, key);
        return int.TryParse(value, out var parsed) && parsed > 0;
    }

    private static IEnumerable<StepVisit> EnumerateSteps(IReadOnlyList<StepModel> steps, string basePath)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var path = $"{basePath}/{index}";
            yield return new StepVisit(step, path, index, steps.Count);

            foreach (var child in EnumerateSteps(step.Children, $"{path}/children"))
            {
                yield return child;
            }
        }
    }

    private sealed record StepVisit(StepModel Step, string Path, int Index, int SiblingCount);
}
