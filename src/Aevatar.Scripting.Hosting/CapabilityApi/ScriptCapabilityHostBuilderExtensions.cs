using Aevatar.Hosting;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

public static class ScriptCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddScriptingCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "scripting-bundle",
            Category = "capability",
            RequiredRoutes =
            [
                "/api/scripts/evolutions/proposals",
            ],
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                // Refactor (iter56/cluster-928-script-any-public-delete): old=public Any → JSON readmodel surface,
                // new=removed (keep internal semantic Any).
                // Studio activity now uses native AppScriptReadModel data.
                await Task.CompletedTask.WaitAsync(cancellationToken);
                return AevatarHealthContributorResult.Healthy("Scripting capability is ready.");
            },
        });

        return builder.AddAevatarCapability(
            name: "scripting-bundle",
            configureServices: static (services, configuration) => services.AddScriptCapability(configuration),
            mapEndpoints: static app => app.MapScriptCapabilityEndpoints());
    }
}
