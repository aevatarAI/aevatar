using Aevatar.Configuration;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Bootstrap.Hosting;

public sealed class AevatarDefaultHostOptions
{
    public string ServiceName { get; set; } = "Aevatar.Host.Api";

    public bool EnableCors { get; set; } = true;

    public string CorsPolicyName { get; set; } = "Default";

    public bool EnableWebSockets { get; set; }

    public bool EnableConnectorBootstrap { get; set; } = true;

    public bool AutoMapCapabilities { get; set; } = true;

    public bool MapRootHealthEndpoint { get; set; } = true;

    public string DefaultListenUrls { get; set; } = string.Empty;
}

public static class WebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddAevatarDefaultHost(
        this WebApplicationBuilder builder,
        Action<AevatarDefaultHostOptions>? configureHost = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var hostOptions = new AevatarDefaultHostOptions();
        configureHost?.Invoke(hostOptions);

        AddApplicationBaseConfiguration(builder);
        builder.Configuration.AddAevatarConfig();
        ApplyDefaultListenUrls(builder, hostOptions);
        builder.Services.AddAevatarBootstrap(builder.Configuration);
        builder.Services.AddSingleton(hostOptions);

        if (hostOptions.EnableConnectorBootstrap)
            builder.Services.AddHostedService<ConnectorBootstrapHostedService>();

        if (hostOptions.EnableCors)
            AddDefaultCorsPolicy(builder, hostOptions.CorsPolicyName);

        return builder;
    }

    private static void ApplyDefaultListenUrls(
        WebApplicationBuilder builder,
        AevatarDefaultHostOptions hostOptions)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.DefaultListenUrls))
            return;

        if (HasExplicitListenConfiguration(builder.Configuration))
            return;

        builder.WebHost.UseUrls(hostOptions.DefaultListenUrls);
    }

    private static void AddApplicationBaseConfiguration(WebApplicationBuilder builder)
    {
        var applicationBasePath = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(applicationBasePath) || !Directory.Exists(applicationBasePath))
            return;

        builder.Configuration.AddJsonFile(
            Path.Combine(applicationBasePath, "appsettings.json"),
            optional: true,
            reloadOnChange: false);

        var environmentName = builder.Environment.EnvironmentName?.Trim();
        if (string.IsNullOrWhiteSpace(environmentName))
            return;

        builder.Configuration.AddJsonFile(
            Path.Combine(applicationBasePath, $"appsettings.{environmentName}.json"),
            optional: true,
            reloadOnChange: false);
    }

    private static bool HasExplicitListenConfiguration(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration[WebHostDefaults.ServerUrlsKey]))
            return true;

        if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) ||
            !string.IsNullOrWhiteSpace(configuration["https_ports"]))
            return true;

        return configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();
    }

    public static WebApplication UseAevatarDefaultHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<AevatarDefaultHostOptions>();
        if (options.EnableCors)
            app.UseCors(options.CorsPolicyName);

        if (options.EnableWebSockets)
            app.UseWebSockets();

        if (options.MapRootHealthEndpoint)
            app.MapGet("/", () => Results.Ok(new { name = options.ServiceName, status = "running" }));

        if (options.AutoMapCapabilities)
            app.MapAevatarCapabilities();

        return app;
    }

    private static void AddDefaultCorsPolicy(WebApplicationBuilder builder, string policyName)
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        builder.Services.AddCors(o => o.AddPolicy(policyName, p =>
        {
            if (builder.Environment.IsDevelopment())
            {
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                return;
            }

            if (allowedOrigins is { Length: > 0 })
            {
                p.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
                return;
            }

            p.SetIsOriginAllowed(_ => false);
        }));
    }
}
