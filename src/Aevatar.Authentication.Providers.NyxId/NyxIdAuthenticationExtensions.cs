using Aevatar.Authentication.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Authentication.Providers.NyxId;

public static class NyxIdAuthenticationExtensions
{
    /// <summary>
    /// Registers the NyxID claims transformer.
    /// Call this in the host composition root to enable NyxID scope claim mapping.
    /// </summary>
    public static IServiceCollection AddNyxIdAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<IAevatarClaimsTransformer, NyxIdClaimsTransformer>();
        return services;
    }
}
