using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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

    public static IApplicationBuilder MapScopeServiceTokenJwks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetService<IOptions<ScopeServiceTokenOptions>>()?.Value;
        if (options == null)
            return app;

        if (!options.Enabled)
            return app;

        var path = NormalizePath(options.JwksPath);
        app.MapGet(path, (IScopeServiceTokenKeyProvider keyProvider) =>
            Results.Json(new
            {
                keys = keyProvider.ValidationKeys
                    .Where(static key => key.ValidationKey is AsymmetricSecurityKey)
                    .Select(key =>
                    {
                        var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(key.ValidationKey);
                        jwk.Kid = key.Kid;
                        jwk.Alg = key.Algorithm;
                        jwk.KeyOps.Clear();
                        jwk.KeyOps.Add("verify");
                        return jwk;
                    })
                    .ToArray(),
            })).AllowAnonymous();

        return app;
    }

    private static string NormalizePath(string? path)
    {
        var normalized = path?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "/.well-known/aevatar-scope-service-jwks.json";

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : "/" + normalized;
    }
}
