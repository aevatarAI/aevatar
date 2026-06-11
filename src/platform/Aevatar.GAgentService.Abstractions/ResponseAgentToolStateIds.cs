using System.Text;

namespace Aevatar.GAgentService.Abstractions;

public static class ResponseAgentToolStateIds
{
    private const string Prefix = "responses-agent-tools-";

    /// <summary>
    /// Build a deterministic actor id from <paramref name="scopeId"/> and
    /// <paramref name="ownerSubject"/>. The authoritative scope/owner facts
    /// remain on <see cref="ResponsesAgentToolStateRecord"/>; this id is only
    /// the human-readable actor address.
    /// </summary>
    public static string BuildActorId(string scopeId, string ownerSubject)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedOwnerSubject = NormalizeRequired(ownerSubject, nameof(ownerSubject));

        // Refactor (iter97/cluster-641): Old pattern: opaque SHA-256 actor id made operator lookup/debugging depend on readmodel fields.
        // New principle: actor id is a percent-encoded readable address; typed scope_id / owner_subject state remains the business fact source.
        return $"{Prefix}scope:{Uri.EscapeDataString(normalizedScopeId)}|owner:{Uri.EscapeDataString(normalizedOwnerSubject)}";
    }

    public static string BuildLegacyActorId(string scopeId, string ownerSubject)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedOwnerSubject = NormalizeRequired(ownerSubject, nameof(ownerSubject));

        var input = $"{normalizedScopeId}\n{normalizedOwnerSubject}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Prefix + Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    public static string NewTaskId() => "task_" + Guid.NewGuid().ToString("N");

    public static string NewWebTraceId() => "web_" + Guid.NewGuid().ToString("N");

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} is required.", paramName);

        return value.Trim();
    }
}
