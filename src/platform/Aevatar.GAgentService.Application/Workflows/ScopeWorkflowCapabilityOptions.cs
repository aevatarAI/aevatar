using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowCapabilityOptions
{
    public const string SectionName = "ScopeWorkflowServices";

    public string ServiceAppId { get; set; } = "default";

    public string ServiceNamespace { get; set; } = "default";

    public string DefaultServiceId { get; set; } = "default";

    public string DefinitionActorIdPrefix { get; set; } = "scope-workflow";

    public int ListTake { get; set; } = 200;

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
