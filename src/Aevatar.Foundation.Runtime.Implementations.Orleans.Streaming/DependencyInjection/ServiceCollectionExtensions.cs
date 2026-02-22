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

        services.Replace(ServiceDescriptor.Singleton<IStreamForwardingRegistry, OrleansDistributedStreamForwardingRegistry>());
        services.AddAevatarOrleansStreamProviderAdapter();
        return services;
    }

    public static IServiceCollection AddAevatarOrleansStreamProviderAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IStreamProvider, OrleansStreamProviderAdapter>());
        services.Replace(ServiceDescriptor.Singleton<IStreamLifecycleManager, StreamProviderLifecycleManager>());
        return services;
    }
}
