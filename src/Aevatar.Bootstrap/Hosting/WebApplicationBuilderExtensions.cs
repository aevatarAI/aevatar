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

    public bool AutoMapCapabilities { get; set; } = true;
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

        builder.Configuration.AddAevatarConfig();
        builder.Services.AddAevatarBootstrap(builder.Configuration);
        builder.Services.AddSingleton(hostOptions);

        if (hostOptions.EnableCors)
            AddDefaultCorsPolicy(builder, hostOptions.CorsPolicyName);

        return builder;
    }

    public static WebApplication UseAevatarDefaultHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<AevatarDefaultHostOptions>();
        if (options.EnableCors)
            app.UseCors(options.CorsPolicyName);

        if (options.EnableWebSockets)
            app.UseWebSockets();

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
