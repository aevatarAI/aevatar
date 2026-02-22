using Aevatar.Hosting;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddWorkflowCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "workflow",
            configureServices: static (services, configuration) => services.AddWorkflowCapability(configuration),
            mapEndpoints: static app => app.MapWorkflowCapabilityEndpoints());
    }
}
