using Aevatar.Hosting;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddWorkflowCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "workflow-bundle",
            configureServices: static (services, configuration) => services.AddWorkflowCapability(configuration),
            mapEndpoints: static app => app.MapWorkflowCapabilityEndpoints());
    }
}
