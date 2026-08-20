using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleansStreaming(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<
            OrleansDistributedStreamForwardingRegistry,
            OrleansDistributedStreamForwardingRegistry>());
        services.Replace(ServiceDescriptor.Singleton<IStreamForwardingRegistry>(sp =>
            sp.GetRequiredService<OrleansDistributedStreamForwardingRegistry>()));
        services.Replace(ServiceDescriptor.Singleton<IStreamForwardingBindingAuthority>(sp =>
            sp.GetRequiredService<OrleansDistributedStreamForwardingRegistry>()));
        services.AddAevatarOrleansStreamProviderAdapter();
        return services;
    }

    public static IServiceCollection AddAevatarOrleansStreamProviderAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IStreamProvider, OrleansStreamProviderAdapter>());
        services.Replace(ServiceDescriptor.Singleton<OrleansStreamProviderAdapter>(sp =>
            (OrleansStreamProviderAdapter)sp.GetRequiredService<IStreamProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IStreamLifecycleManager, StreamProviderLifecycleManager>());
        return services;
    }
}
