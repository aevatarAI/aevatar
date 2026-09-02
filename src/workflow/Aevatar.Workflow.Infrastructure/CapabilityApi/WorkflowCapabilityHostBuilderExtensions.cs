using Aevatar.BackendConsole.Hosting;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddWorkflowCapabilityBundle(
        this WebApplicationBuilder builder,
        bool mapChatPost = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

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
                var indexProbe = serviceProvider.GetService<IProjectionIndexConsistencyProbe<WorkflowExecutionCurrentStateDocument>>();
                if (indexProbe != null)
                {
                    var consistency = await indexProbe.CheckIndexConsistencyAsync(cancellationToken);
                    return ProjectionIndexDiagnostics.ToContributorResult(consistency);
                }

                var queryService = serviceProvider.GetRequiredService<IWorkflowExecutionQueryApplicationService>();
                try
                {
                    _ = await queryService.ListAgentsAsync(cancellationToken);
                    return AevatarHealthContributorResult.Healthy("Workflow capability is ready.");
                }
                catch (ProjectionIndexSchemaDriftException exception)
                {
                    return ProjectionIndexDiagnostics.ToUnhealthyContributorResult(exception);
                }
            },
        });

        return builder.AddAevatarCapability(
            name: "workflow-bundle",
            configureServices: static (services, configuration) => services.AddWorkflowCapability(configuration),
            mapEndpoints: app => app.MapWorkflowCapabilityEndpoints(mapChatPost));
    }
}
