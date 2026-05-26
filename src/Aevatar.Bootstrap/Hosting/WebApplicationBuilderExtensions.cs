using Aevatar.Configuration;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
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

    public bool EnableHealthEndpoints { get; set; } = true;

    public string LivenessEndpointRoute { get; set; } = "/health/live";

    public string ReadinessEndpointRoute { get; set; } = "/health/ready";

    public bool EnableOpenApiDocument { get; set; } = true;

    public string OpenApiDocumentRoute { get; set; } = "/api/openapi.json";

    /// <summary>
    /// Whether the host may use the local file secrets store
    /// (<c>~/.aevatar/secrets.json</c>) and register
    /// <c>AevatarSecretsStore</c>.
    /// <para>
    /// <c>true</c> (default): legacy behavior — secrets.json is loaded into
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> and a
    /// read/write store is registered. Suitable for local dev, CLI tools,
    /// localnet, and demos.
    /// </para>
    /// <para>
    /// <c>false</c>: production / mainnet hosts. The host must not load or
    /// persist secrets to disk. <c>secrets.json</c> is skipped and the
    /// read-only <c>EnvironmentSecretsStore</c> is registered; secrets must
    /// come from configuration / <c>AEVATAR_</c>-prefixed environment
    /// variables. Mutation methods on the store throw on call.
    /// </para>
    /// </summary>
    public bool AllowLocalFileSecretsStore { get; set; } = true;
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
        builder.Configuration.AddAevatarConfig(allowLocalFileStore: hostOptions.AllowLocalFileSecretsStore);
        builder.Services.AddAevatarBootstrap(
            builder.Configuration,
            allowLocalFileSecretsStore: hostOptions.AllowLocalFileSecretsStore);
        builder.Services.AddSingleton(hostOptions);
        builder.Services.AddSingleton(new AevatarHostMetadata
        {
            ServiceName = hostOptions.ServiceName,
        });
        builder.Services.AddSingleton<AevatarHostHealthService>();

        if (hostOptions.EnableConnectorBootstrap)
            builder.Services.AddHostedService<ConnectorBootstrapHostedService>();

        if (hostOptions.EnableCors)
            AddDefaultCorsPolicy(builder, hostOptions.CorsPolicyName);

        if (hostOptions.EnableOpenApiDocument)
            builder.Services.AddOpenApi();

        // Authorization services are always required because UseAuthorization() runs
        // unconditionally in UseAevatarDefaultHost. This is safe even when no auth
        // scheme is registered -- endpoints without RequireAuthorization remain open.
        builder.Services.AddAuthorization();

        return builder;
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

    public static WebApplication UseAevatarDefaultHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<AevatarDefaultHostOptions>();
        if (options.EnableCors)
            app.UseCors(options.CorsPolicyName);

        // Authentication middleware: auto-activates when an auth scheme is registered.
        // This keeps Bootstrap decoupled from any specific auth provider.
        if (app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() != null)
        {
            app.UseAuthentication();
        }

        // Authorization middleware must always run so that [Authorize] attributes produce
        // a proper 401/403 instead of an unhandled 500 when no auth scheme is configured.
        app.UseAuthorization();

        if (options.EnableWebSockets)
            app.UseWebSockets();

        if (options.MapRootHealthEndpoint)
        {
            app.MapGet("/", () => TypedResults.Ok(new AevatarHostStatusResponse(
                    options.ServiceName,
                    "running")))
                .WithTags("Host")
                .WithName("GetHostStatus")
                .WithSummary("Get host process status.")
                .Produces<AevatarHostStatusResponse>(StatusCodes.Status200OK)
                .AllowAnonymous();
        }

        if (options.EnableHealthEndpoints)
        {
            app.MapGet(options.LivenessEndpointRoute, async (
                    AevatarHostHealthService healthService,
                    CancellationToken cancellationToken) =>
                    TypedResults.Ok(await healthService.GetLivenessAsync(cancellationToken)))
                .WithTags("Health")
                .WithName("GetHostLiveness")
                .WithSummary("Get liveness status for the current host process.")
                .Produces<AevatarHealthResponse>(StatusCodes.Status200OK)
                .AllowAnonymous();

            app.MapGet(options.ReadinessEndpointRoute, async (
                    AevatarHostHealthService healthService,
                    CancellationToken cancellationToken) =>
                    (await healthService.GetReadinessAsync(cancellationToken)).ToHttpResult())
                .WithTags("Health")
                .WithName("GetHostReadiness")
                .WithSummary("Get readiness status for the current host and its registered API capabilities.")
                .Produces<AevatarHealthResponse>(StatusCodes.Status200OK)
                .Produces<AevatarHealthResponse>(StatusCodes.Status503ServiceUnavailable)
                .AllowAnonymous();
        }

        if (options.EnableOpenApiDocument)
            app.MapOpenApi(options.OpenApiDocumentRoute).AllowAnonymous();

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
