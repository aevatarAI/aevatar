using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.DynamicRuntime.Hosting.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.DynamicRuntime.Extensions.Hosting;

public static class DynamicRuntimeCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddDynamicRuntimeCapabilityWithAIDefaults(
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

        builder.AddDynamicRuntimeCapability();
        return builder;
    }
}
