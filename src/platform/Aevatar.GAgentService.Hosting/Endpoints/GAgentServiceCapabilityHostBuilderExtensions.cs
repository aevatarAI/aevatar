using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class GAgentServiceCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddGAgentServiceCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "gagent-service",
            Category = "capability",
            RequiredRoutes =
            [
                "/api/services",
                "/api/scopes/{scopeId}/binding",
                "/api/scopes/{scopeId}/workflows",
                "/api/scopes/{scopeId}/scripts",
            ],
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                var lifecycleQueryPort = serviceProvider.GetRequiredService<IServiceLifecycleQueryPort>();
                _ = await lifecycleQueryPort.ListServicesAsync(string.Empty, string.Empty, string.Empty, 1, cancellationToken);

                var scopeWorkflowQueryPort = serviceProvider.GetRequiredService<IScopeWorkflowQueryPort>();
                _ = await scopeWorkflowQueryPort.ListAsync("health", cancellationToken);

                var scopeScriptQueryPort = serviceProvider.GetRequiredService<IScopeScriptQueryPort>();
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
