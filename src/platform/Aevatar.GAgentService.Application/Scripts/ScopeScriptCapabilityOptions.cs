using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Application.Scripts;

public sealed class ScopeScriptCapabilityOptions
{
    public const string SectionName = "ScopeScriptServices";

    public string CatalogActorIdPrefix { get; set; } = "user-script-catalog";

    public string DefinitionActorIdPrefix { get; set; } = "user-script-definition";

    public int ListTake { get; set; } = 200;

    public string BuildCatalogActorId(string scopeId) =>
        $"{CatalogActorIdPrefix}:{BuildOpaqueToken(scopeId)}";

    public string BuildDefinitionActorId(string scopeId, string scriptId, string revisionId) =>
        $"{DefinitionActorIdPrefix}:{BuildOpaqueToken(scopeId)}:{BuildOpaqueToken(scriptId)}:{BuildOpaqueToken(revisionId)}";

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
