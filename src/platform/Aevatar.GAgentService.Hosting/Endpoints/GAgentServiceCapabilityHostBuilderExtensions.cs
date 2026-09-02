using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class GAgentServiceCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddGAgentServiceCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Scope script routes and the script query probe exist only when the host composed
        // the scripting capability before this bundle (see AddGAgentServiceCapability).
        var scriptingCapabilityRegistered = builder.Services.Any(x =>
            x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker));
        string[] requiredRoutes = scriptingCapabilityRegistered
            ?
            [
                "/api/services",
                "/api/scopes/{scopeId}/binding",
                "/api/scopes/{scopeId}/workflows",
                "/api/scopes/{scopeId}/scripts",
                "/api/schedules",
                "/api/schedules/{scheduleId}",
            ]
            :
            [
                "/api/services",
                "/api/scopes/{scopeId}/binding",
                "/api/scopes/{scopeId}/workflows",
                "/api/schedules",
                "/api/schedules/{scheduleId}",
            ];

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "gagent-service",
            Category = "capability",
            RequiredRoutes = requiredRoutes,
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                var lifecycleQueryPort = serviceProvider.GetRequiredService<IServiceLifecycleQueryPort>();
                _ = await lifecycleQueryPort.ListServicesAsync(string.Empty, string.Empty, string.Empty, 1, cancellationToken);

                var scopeWorkflowQueryPort = serviceProvider.GetRequiredService<IScopeWorkflowQueryPort>();
                _ = await scopeWorkflowQueryPort.ListAsync("health", cancellationToken);

                if (serviceProvider.GetService<IScopeScriptQueryPort>() is { } scopeScriptQueryPort)
                    _ = await scopeScriptQueryPort.ListAsync("health", cancellationToken);

                return AevatarHealthContributorResult.Healthy("GAgent service capability is ready.");
            },
        });

        return builder.AddAevatarCapability(
            "gagent-service",
            static (services, configuration) => services.AddGAgentServiceCapability(configuration),
            static app => app.MapGAgentServiceEndpoints());
    }
}
