namespace Aevatar.Authentication.Abstractions;

// Provider-agnostic admin authorization seam. The identity provider resolves the current user from the caller's
// bearer token; aevatar-owned policy decides whether that identity is an aevatar admin.
public interface IPlatformAdminAuthorizer
{
    /// <summary>
    /// Resolves the caller's identity from the bearer token and applies aevatar admin policy. MUST fail closed:
    /// returns a non-elevated <see cref="PlatformCaller"/> on a missing/blank token, provider error,
    /// malformed identity, or missing grant. Only <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default);
}

public static class PlatformAdminGrantSources
{
    public const string AllowedUserId = "allowed_user_id";
    public const string AllowedEmail = "allowed_email";
    public const string NyxIdPlatformRole = "nyxid_platform_role";
}

// Result of resolving a caller's current-user identity and aevatar admin policy decision.
public sealed record PlatformCaller(
    bool IsElevated,
    string Role,
    string Email,
    string UserId,
    string GrantSource = "")
{
    public static PlatformCaller NotElevated { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty);
}
