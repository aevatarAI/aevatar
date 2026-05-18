using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services required by the A2A protocol adapter layer.
    /// Prerequisites: the host must have already registered <c>IActorDispatchPort</c> and <c>IA2ATaskStore</c>.
    /// </summary>
    public static IServiceCollection AddA2AAdapter(this IServiceCollection services)
    {
        services.TryAddScoped<IA2AAdapterService, A2AAdapterService>();
        return services;
    }

    /// <summary>
    /// Registers the process-local A2A task store for development and tests only.
    /// </summary>
    public static IServiceCollection AddInMemoryA2ATaskStoreForDevelopment(this IServiceCollection services)
    {
        // Refactor (iter6/cluster-013):
        //   Old pattern: AddA2AAdapter silently installed a process-local task fact store.
        //   New principle: in-memory task facts require an explicit dev/test opt-in.
        services.TryAddSingleton<IA2ATaskStore>(_ => new InMemoryA2ATaskStore());
        return services;
    }
}
