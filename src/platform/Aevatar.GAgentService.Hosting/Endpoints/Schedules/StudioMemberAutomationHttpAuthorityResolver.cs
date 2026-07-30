using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints.Schedules;

public sealed record StudioMemberAutomationHttpAuthority(
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner,
    string ProvisioningBearerToken);

public sealed class StudioMemberAutomationAuthorizationBindingRequiredException : UnauthorizedAccessException
{
    public StudioMemberAutomationAuthorizationBindingRequiredException()
        : base("Reconnect NyxID to authorize this automation.")
    {
    }
}

public static class StudioMemberAutomationHttpAuthorityResolver
{
    public static Task<StudioMemberAutomationHttpAuthority> ResolveAsync(
        HttpContext http,
        IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct = default) =>
        ResolveAsync(http, bindingQuery, null, ct);

    public static async Task<StudioMemberAutomationHttpAuthority> ResolveAsync(
        HttpContext http,
        IExternalIdentityBindingQueryPort bindingQuery,
        string? fallbackSubjectExternalUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bindingQuery);

        var subject = ResolveHttpSubject(http) ?? NormalizeOptional(fallbackSubjectExternalUserId);
        if (subject is null)
            throw new UnauthorizedAccessException("nyxid_subject_missing");

        var binding = await bindingQuery.ResolveAsync(
            new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = subject,
            },
            ct);
        var bindingId = NormalizeOptional(binding?.Value) ??
            BuildFallbackBindingId(fallbackSubjectExternalUserId, subject);
        if (bindingId is null)
            throw new StudioMemberAutomationAuthorizationBindingRequiredException();

        return new StudioMemberAutomationHttpAuthority(
            new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = subject,
                },
                OwnerScope.NyxIdPlatform,
                string.Empty,
                subject,
                bindingId),
            IsAuthDisabledFallback(bindingId, subject)
                ? subject
                : ResolveBearerToken(http));
    }

    private static string? ResolveHttpSubject(HttpContext http) =>
        NormalizeOptional(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value) ??
        NormalizeOptional(http.User.FindFirst("sub")?.Value);

    private static string? BuildFallbackBindingId(
        string? fallbackSubjectExternalUserId,
        string subject) =>
        string.Equals(
            NormalizeOptional(fallbackSubjectExternalUserId),
            subject,
            StringComparison.Ordinal)
            ? $"auth-disabled:{subject}"
            : null;

    private static bool IsAuthDisabledFallback(string bindingId, string subject) =>
        string.Equals(bindingId, $"auth-disabled:{subject}", StringComparison.Ordinal);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ResolveBearerToken(HttpContext http)
    {
        var header =
            http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (header == null ||
            !header.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "provisioning_bearer_missing");
        }

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(','))
        {
            throw new UnauthorizedAccessException(
                "provisioning_bearer_invalid");
        }

        return token;
    }
}
