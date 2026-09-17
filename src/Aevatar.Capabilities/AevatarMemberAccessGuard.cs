using Microsoft.AspNetCore.Http;

namespace Aevatar.Capabilities;

public static class AevatarMemberAccessGuard
{
    public static bool TryCreateMemberAccessDeniedResult(
        HttpContext http,
        string scopeId,
        string memberId,
        out IResult denied)
    {
        ValidateRequired(memberId, nameof(memberId));
        // A Studio member is a scoped resource, not the caller identity. The member resolver that
        // follows this guard validates (scopeId, memberId) against the authoritative read model.
        return AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out denied);
    }

    public static Task<bool> TryWriteMemberAccessDeniedAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        CancellationToken ct)
    {
        ValidateRequired(memberId, nameof(memberId));
        return AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct);
    }

    private static void ValidateRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");
    }
}
