namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Resolves the authenticated caller's service identity context for the
/// <c>/api/services/**</c> HTTP surface.
/// </summary>
public interface IServiceIdentityContextResolver
{
    ServiceIdentityContext? Resolve();

    bool TryResolveAuthenticatedScopeRequestContext(
        string? fallbackTenantId,
        string? fallbackAppId,
        string? fallbackNamespace,
        out ServiceIdentityContext context,
        out string? failure);

    bool TryGetAuthenticatedIdentityFailure(out string message);
}

public sealed record ServiceIdentityContext(
    string TenantId,
    string AppId,
    string Namespace,
    string Source);
