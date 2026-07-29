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

public static class StudioMemberAutomationHttpAuthorityResolver
{
    public static async Task<StudioMemberAutomationHttpAuthority> ResolveAsync(
        HttpContext http,
        IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bindingQuery);

        var subject =
            http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("nyxid_subject_missing");

        var normalizedSubject = subject.Trim();
        var binding = await bindingQuery.ResolveAsync(
            new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = normalizedSubject,
            },
            ct);
        if (binding == null || string.IsNullOrWhiteSpace(binding.Value))
            throw new UnauthorizedAccessException("nyxid_binding_missing");

        return new StudioMemberAutomationHttpAuthority(
            new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = normalizedSubject,
                },
                OwnerScope.NyxIdPlatform,
                string.Empty,
                normalizedSubject,
                binding.Value.Trim()),
            ResolveBearerToken(http));
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
