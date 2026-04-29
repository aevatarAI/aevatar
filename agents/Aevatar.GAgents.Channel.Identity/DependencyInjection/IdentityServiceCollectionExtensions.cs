using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Channel.Identity.DependencyInjection;

/// <summary>
/// DI extensions for the Channel.Identity module. Composition root for the
/// per-user NyxID binding scaffolding (ADR-0017). Production wiring of
/// <c>NyxIdRemoteCapabilityBroker</c>, the projection, the OAuth callback
/// endpoint, and the slash-command routing will be added in subsequent PRs
/// (gated on ChronoAIProject/NyxID#549 contract freeze).
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Channel.Identity module's actor + interfaces. Today this
    /// only declares the seam; concrete adapters (broker, query port,
    /// projection-readiness port) are added in follow-up PRs.
    /// </summary>
    public static IServiceCollection AddChannelIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Implementation registrations land in subsequent PRs. The actor itself
        // is wired via the standard GAgent runtime registration; nothing extra
        // is needed here yet.
        return services;
    }
}
