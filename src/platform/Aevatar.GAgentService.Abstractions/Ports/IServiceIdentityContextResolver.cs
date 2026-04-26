namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Resolves the authenticated caller's service identity context for the
/// <c>/api/services/**</c> HTTP surface.
/// </summary>
public interface IServiceIdentityContextResolver
{
    ServiceIdentityContext? Resolve();

    bool TryGetAuthenticatedIdentityFailure(out string message);
}

public sealed record ServiceIdentityContext(
    string TenantId,
    string AppId,
    string Namespace,
    string Source);
