using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Orleans.Configuration;

namespace Sisyphus.Host.Hosting;

public static class SisyphusOrleansHostBuilderExtensions
{
    public static WebApplicationBuilder AddSisyphusOrleansHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var runtimeOptions = new AevatarActorRuntimeOptions();

        var configuredProvider = builder.Configuration[$"{AevatarActorRuntimeOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            runtimeOptions.Provider = configuredProvider;

        if (!string.Equals(runtimeOptions.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
            return builder;

        var configuredPersistenceBackend = builder.Configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"];
        if (!string.IsNullOrWhiteSpace(configuredPersistenceBackend))
            runtimeOptions.OrleansPersistenceBackend = configuredPersistenceBackend;

        var configuredGarnetConnectionString = builder.Configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"];
        if (!string.IsNullOrWhiteSpace(configuredGarnetConnectionString))
            runtimeOptions.OrleansGarnetConnectionString = configuredGarnetConnectionString;

        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering(
                serviceId: builder.Configuration["Orleans:ServiceId"] ?? "sisyphus-host",
                clusterId: builder.Configuration["Orleans:ClusterId"] ?? "sisyphus-cluster");

            // LLM streaming + tool calling loops can take 2-5 minutes per step.
            // The Orleans default ResponseTimeout is 30s, which kills in-flight LLM calls.
            siloBuilder.Configure<SiloMessagingOptions>(options =>
            {
                options.ResponseTimeout = TimeSpan.FromMinutes(5);
            });

            siloBuilder.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.PersistenceBackend = runtimeOptions.OrleansPersistenceBackend;
                orleansOptions.GarnetConnectionString = runtimeOptions.OrleansGarnetConnectionString;
            });
        });

        return builder;
    }
}
