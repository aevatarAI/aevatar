using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowCapabilityOptions
{
    public const string SectionName = "ScopeWorkflowServices";
    public const string FixedServiceAppId = ScopeServiceIdentityDefaults.ServiceAppId;
    public const string FixedServiceNamespace = ScopeServiceIdentityDefaults.ServiceNamespace;

    // Keep the setter for configuration binding/object initializers, but pin the runtime identity.
    public string ServiceAppId
    {
        get => FixedServiceAppId;
        set
        {
        }
    }

    // Keep the setter for configuration binding/object initializers, but pin the runtime identity.
    public string ServiceNamespace
    {
        get => FixedServiceNamespace;
        set
        {
        }
    }

    public string DefaultServiceId { get; set; } = "default";

    public string DefinitionActorIdPrefix { get; set; } = "scope-workflow";

    public int ListTake { get; set; } = 200;

    public List<ScopeWorkflowConfiguredTemplateOptions> ConfiguredTemplates { get; set; } = [];

    public string BuildDefinitionActorIdPrefix(string scopeId, string workflowId) =>
        $"{DefinitionActorIdPrefix}:{BuildOpaqueToken(scopeId)}:{BuildOpaqueToken(workflowId)}";

    public static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string BuildOpaqueToken(string value)
    {
        var normalized = NormalizeRequired(value, nameof(value));
        var slug = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                slug.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (slug.Length == 0 || slug[^1] == '-')
                continue;

            slug.Append('-');
        }

        while (slug.Length > 0 && slug[^1] == '-')
            slug.Length--;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hashSuffix = Convert.ToHexString(hash).ToLowerInvariant()[..10];
        return slug.Length == 0 ? hashSuffix : $"{slug}-{hashSuffix}";
    }
}

public sealed class ScopeWorkflowConfiguredTemplateOptions
{
    public bool Enabled { get; set; } = true;

    public string WorkflowId { get; set; } = string.Empty;

    public string RevisionId { get; set; } = string.Empty;

    public string WorkflowYaml { get; set; } = string.Empty;

    public string WorkflowYamlPath { get; set; } = string.Empty;

    public string WorkflowName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public bool? ExposureDesired { get; set; }
}
