using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Workflow.Extensions.AIProjection;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Workflow.Extensions.Hosting;

public static class WorkflowCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddWorkflowCapabilityWithAIDefaults(
        this WebApplicationBuilder builder,
        Action<AevatarAIFeatureOptions>? configureAIFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAevatarAIFeatures(builder.Configuration, options =>
        {
            options.EnableMEAIProviders = true;
            options.EnableMCPTools = true;
            options.EnableSkills = true;
            configureAIFeatures?.Invoke(options);
        });
        builder.Services.AddWorkflowProjectionReadModelProviders(builder.Configuration);
        builder.AddWorkflowCapability();
        builder.AddScriptCapability();
        builder.Services.AddWorkflowAIProjectionExtensions();

        return builder;
    }
}
