using Aevatar.Bootstrap.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Workflow.Host.Api;

internal static class ObservabilityExtensions
{
    internal static WebApplicationBuilder AddAevatarWorkflowObservability(
        this WebApplicationBuilder builder,
        string defaultServiceName = "Aevatar.Workflow.Host.Api") =>
        builder.AddAevatarHostObservability(defaultServiceName, "Aevatar.Workflow");
}
