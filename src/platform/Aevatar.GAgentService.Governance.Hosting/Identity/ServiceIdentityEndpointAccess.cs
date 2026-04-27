using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Governance.Hosting.Identity;

public sealed class DefaultServiceIdentityContextResolver : IServiceIdentityContextResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    private static readonly (string ClaimType, string DisplayName)[] IdentityClaims =
    [
        (AevatarStandardClaimTypes.TenantId, nameof(AevatarStandardClaimTypes.TenantId)),
        (AevatarStandardClaimTypes.AppId, nameof(AevatarStandardClaimTypes.AppId)),
        (AevatarStandardClaimTypes.Namespace, nameof(AevatarStandardClaimTypes.Namespace)),
    ];

    public DefaultServiceIdentityContextResolver(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public ServiceIdentityContext? Resolve()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        if (!TryGetSingleClaimValue(user, AevatarStandardClaimTypes.TenantId, out var tenantId, out _)
            || !TryGetSingleClaimValue(user, AevatarStandardClaimTypes.AppId, out var appId, out _)
            || !TryGetSingleClaimValue(user, AevatarStandardClaimTypes.Namespace, out var @namespace, out _))
        {
            return null;
        }

        return new ServiceIdentityContext(
            tenantId,
            appId,
            @namespace,
            $"claims:{AevatarStandardClaimTypes.TenantId}/{AevatarStandardClaimTypes.AppId}/{AevatarStandardClaimTypes.Namespace}");
    }

    public bool TryGetAuthenticatedIdentityFailure(out string message)
    {
        message = string.Empty;
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        var failures = IdentityClaims
            .Select(descriptor => TryGetSingleClaimValue(user, descriptor.ClaimType, out _, out var failure)
                ? null
                : failure ?? $"Authenticated service identity claim '{descriptor.ClaimType}' is missing.")
            .Where(static failure => !string.IsNullOrWhiteSpace(failure))
            .ToArray();
        if (failures.Length == 0)
            return false;

        message = string.Join(" ", failures);
        return true;
    }

    private static bool TryGetSingleClaimValue(
        ClaimsPrincipal user,
        string claimType,
        out string value,
        out string? failure)
    {
        value = string.Empty;
        failure = null;

        var values = user.Claims
            .Where(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static claimValue => !string.IsNullOrWhiteSpace(claimValue))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 1)
        {
            value = values[0]!;
            return true;
        }

        failure = values.Length == 0
            ? $"Authenticated service identity claim '{claimType}' is missing."
            : $"Authenticated service identity claim '{claimType}' is ambiguous.";
        return false;
    }
}

public static class ServiceIdentityEndpointAccess
{
    /// <summary>
    /// Resolves the owner identity context. When <paramref name="authenticatedContext"/> is
    /// supplied (the caller already invoked <c>resolver.Resolve()</c>), claim resolution is
    /// reused — avoiding the double-Resolve cost when the handler also needs the authenticated
    /// context for validation (e.g., <c>TryValidateOwnerIdentity</c>). Pass <c>null</c> to fall
    /// through to the original behaviour: when authenticated but claims are missing/ambiguous,
    /// returns <c>403 SERVICE_IDENTITY_ACCESS_DENIED</c>; when unauthenticated, falls back to
    /// the request's tenant/app/namespace fields.
    /// </summary>
    public static bool TryResolveContext(
        IServiceIdentityContextResolver resolver,
        ServiceIdentityContext? authenticatedContext,
        string? fallbackTenantId,
        string? fallbackAppId,
        string? fallbackNamespace,
        out ServiceIdentityContext context,
        out IResult denied)
    {
        if (authenticatedContext is { } resolved)
        {
            context = resolved;
            denied = Results.Empty;
            return true;
        }

        if (resolver.TryGetAuthenticatedIdentityFailure(out var failure))
        {
            context = new ServiceIdentityContext(string.Empty, string.Empty, string.Empty, "denied");
            denied = Results.Json(
                new
                {
                    code = "SERVICE_IDENTITY_ACCESS_DENIED",
                    message = failure,
                },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        context = new ServiceIdentityContext(
            fallbackTenantId?.Trim() ?? string.Empty,
            fallbackAppId?.Trim() ?? string.Empty,
            fallbackNamespace?.Trim() ?? string.Empty,
            "request");
        denied = Results.Empty;
        return true;
    }

    public static bool TryResolveContext(
        IServiceIdentityContextResolver resolver,
        string? fallbackTenantId,
        string? fallbackAppId,
        string? fallbackNamespace,
        out ServiceIdentityContext context,
        out IResult denied)
        => TryResolveContext(
            resolver,
            resolver.Resolve(),
            fallbackTenantId,
            fallbackAppId,
            fallbackNamespace,
            out context,
            out denied);

    public static bool TryResolveIdentity(
        IServiceIdentityContextResolver resolver,
        ServiceIdentityContext? authenticatedContext,
        string? fallbackTenantId,
        string? fallbackAppId,
        string? fallbackNamespace,
        string serviceId,
        out ServiceIdentity identity,
        out IResult denied)
    {
        if (!TryResolveContext(
                resolver,
                authenticatedContext,
                fallbackTenantId,
                fallbackAppId,
                fallbackNamespace,
                out var context,
                out denied))
        {
            identity = new ServiceIdentity();
            return false;
        }

        identity = new ServiceIdentity
        {
            TenantId = context.TenantId,
            AppId = context.AppId,
            Namespace = context.Namespace,
            ServiceId = serviceId?.Trim() ?? string.Empty,
        };
        return true;
    }

    public static bool TryResolveIdentity(
        IServiceIdentityContextResolver resolver,
        string? fallbackTenantId,
        string? fallbackAppId,
        string? fallbackNamespace,
        string serviceId,
        out ServiceIdentity identity,
        out IResult denied)
        => TryResolveIdentity(
            resolver,
            resolver.Resolve(),
            fallbackTenantId,
            fallbackAppId,
            fallbackNamespace,
            serviceId,
            out identity,
            out denied);
}
