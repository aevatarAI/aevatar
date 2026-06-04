using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddWorkflowCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "workflow-bundle",
            Category = "capability",
            RequiredRoutes =
            [
                "/api/agents",
                "/api/primitives",
                "/api/workflows",
                "/api/capabilities",
            ],
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                var queryService = serviceProvider.GetRequiredService<IWorkflowExecutionQueryApplicationService>();
                _ = await queryService.ListAgentsAsync(cancellationToken);
                return AevatarHealthContributorResult.Healthy("Workflow capability is ready.");
            },
        });

        return builder.AddAevatarCapability(
            name: "workflow-bundle",
            configureServices: static (services, configuration) => services.AddWorkflowCapability(configuration),
            mapEndpoints: static app => app.MapWorkflowCapabilityEndpoints());
    }
}
