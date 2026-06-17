using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowConnectedServiceFileSubmitOptionsValidator
    : IValidateOptions<WorkflowConnectedServiceFileSubmitOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        WorkflowConnectedServiceFileSubmitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        for (var index = 0; index < options.Targets.Count; index++)
            ValidateTarget(options.Targets[index], index, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTarget(
        WorkflowConnectedServiceFileSubmitTarget target,
        int index,
        List<string> failures)
    {
        var prefix = $"{WorkflowConnectedServiceFileSubmitOptions.SectionName}:Targets[{index}]";
        RequireNonBlank(target.Target, $"{prefix}.Target", failures);
        RequireNonBlank(target.Provider, $"{prefix}.Provider", failures);
        RequireNonBlank(target.OutputField, $"{prefix}.OutputField", failures);

        if (target.MaxFileBytes <= 0)
            failures.Add($"{prefix}.MaxFileBytes must be greater than zero.");
        if (target.AllowedMediaTypes.Count == 0)
            failures.Add($"{prefix}.AllowedMediaTypes must contain at least one media type.");

        foreach (var (argumentName, policy) in target.Arguments)
        {
            if (string.IsNullOrWhiteSpace(argumentName))
                failures.Add($"{prefix}.Arguments contains a blank key.");
            RequireNonBlank(policy.Name, $"{prefix}.Arguments[{argumentName}].Name", failures);
        }

        if (target.Endpoint != null)
            ValidateEndpoint(target.Endpoint, $"{prefix}.Endpoint", failures);
    }

    private static void ValidateEndpoint(
        WorkflowConnectedServiceFileSubmitEndpoint endpoint,
        string prefix,
        List<string> failures)
    {
        RequireNonBlank(endpoint.ServiceSlug, $"{prefix}.ServiceSlug", failures);
        RequireNonBlank(endpoint.Path, $"{prefix}.Path", failures);
        RequireNonBlank(endpoint.Method, $"{prefix}.Method", failures);
        RequireNonBlank(endpoint.FileFieldName, $"{prefix}.FileFieldName", failures);

        if (Uri.TryCreate(endpoint.Path, UriKind.Absolute, out _))
            failures.Add($"{prefix}.Path must be a relative downstream path.");
        if (!string.IsNullOrWhiteSpace(endpoint.Method) &&
            !IsSupportedEndpointMethod(endpoint.Method))
            failures.Add($"{prefix}.Method must be POST, PUT, or PATCH.");
    }

    private static bool IsSupportedEndpointMethod(string method) =>
        string.Equals(method.Trim(), "POST", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method.Trim(), "PUT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method.Trim(), "PATCH", StringComparison.OrdinalIgnoreCase);

    private static void RequireNonBlank(
        string? value,
        string path,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{path} must be configured.");
    }
}
