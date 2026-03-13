using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Workflow.Extensions.AIProjection;
using Aevatar.Workflow.Extensions.Maker;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Workflows;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Workflow.Extensions.Hosting;

public sealed class AevatarPlatformCompositionOptions
{
    public bool EnableAIFeatures { get; set; } = true;

    public bool EnableWorkflowCapability { get; set; } = true;

    public bool EnableScriptingCapability { get; set; } = true;

    public bool EnableWorkflowAIProjection { get; set; } = true;

    public bool EnableMakerExtensions { get; set; }

    public Action<AevatarAIFeatureOptions>? ConfigureAIFeatures { get; set; }
}

public static class AevatarPlatformHostBuilderExtensions
{
    public static WebApplicationBuilder AddAevatarPlatform(
        this WebApplicationBuilder builder,
        Action<AevatarPlatformCompositionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AevatarPlatformCompositionOptions();
        configure?.Invoke(options);

        ValidateOptions(options);

        if (options.EnableAIFeatures)
        {
            builder.Services.AddAevatarAIFeatures(builder.Configuration, aiOptions =>
            {
                aiOptions.EnableMEAIProviders = true;
                aiOptions.EnableMCPTools = true;
                aiOptions.EnableSkills = true;
                options.ConfigureAIFeatures?.Invoke(aiOptions);
            });
        }

        if (options.EnableWorkflowCapability)
        {
            builder.Services.AddWorkflowProjectionReadModelProviders(builder.Configuration);
            builder.AddWorkflowCapabilityBundle();
        }

        if (options.EnableScriptingCapability)
            builder.AddScriptingCapabilityBundle();

        if (options.EnableWorkflowCapability && options.EnableWorkflowAIProjection)
            builder.Services.AddWorkflowAIProjectionExtensions();

        if (options.EnableMakerExtensions)
            builder.Services.AddWorkflowMakerExtensions();

        return builder;
    }

    private static void ValidateOptions(AevatarPlatformCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.EnableMakerExtensions && !options.EnableWorkflowCapability)
        {
            throw new InvalidOperationException(
                "Maker extensions require workflow capability to be enabled.");
        }

        if (options.EnableWorkflowAIProjection && !options.EnableWorkflowCapability)
        {
            throw new InvalidOperationException(
                "Workflow AI projection requires workflow capability to be enabled.");
        }
    }
}
