using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;

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

            siloBuilder.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.PersistenceBackend = runtimeOptions.OrleansPersistenceBackend;
                orleansOptions.GarnetConnectionString = runtimeOptions.OrleansGarnetConnectionString;
            });
        });

        return builder;
    }
}
