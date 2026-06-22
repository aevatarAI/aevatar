using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Authentication.ScopeServiceTokens;

public static class ScopeServiceTokenHostExtensions
{
    public static IServiceCollection AddScopeServiceTokens(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ScopeServiceTokenOptions>()
            .Bind(configuration.GetSection(ScopeServiceTokenOptions.SectionName));
        services.TryAddSingleton<IScopeServiceTokenKeyProvider, ConfiguredScopeServiceTokenKeyProvider>();
        services.TryAddSingleton<IScopeServiceTokenIssuer, ScopeServiceTokenIssuer>();
        return services;
    }
}
