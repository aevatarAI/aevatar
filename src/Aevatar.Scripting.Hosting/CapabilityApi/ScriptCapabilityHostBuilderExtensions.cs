using Aevatar.Hosting;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
                "/api/scripts/runtimes",
                "/api/scripts/runtimes/{actorId}/readmodel",
            ],
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                var queryService = serviceProvider.GetRequiredService<IScriptReadModelQueryApplicationService>();
                _ = await queryService.ListSnapshotsAsync(1, cancellationToken);
                return AevatarHealthContributorResult.Healthy("Scripting capability is ready.");
            },
        });

        return builder.AddAevatarCapability(
            name: "scripting-bundle",
            configureServices: static (services, configuration) => services.AddScriptCapability(configuration),
            mapEndpoints: static app => app.MapScriptCapabilityEndpoints());
    }
}
