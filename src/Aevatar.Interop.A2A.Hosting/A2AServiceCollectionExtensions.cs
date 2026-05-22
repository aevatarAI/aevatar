using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services required by the A2A protocol adapter layer.
    /// Prerequisites: the host must have registered actor dispatch/runtime, projection reader, and event subscription provider.
    /// </summary>
    // Refactor (iter30/cluster-031-a2a-actor-owned):
    //   Old pattern: hosting could opt into process-local IA2ATaskStore lifecycle facts.
    //   New principle: adapter uses existing dispatch/readmodel/subscription ports; lifecycle belongs to task GAgent.
    public static IServiceCollection AddA2AAdapter(this IServiceCollection services)
    {
        services.TryAddScoped<IA2AAdapterService, A2AAdapterService>();
        return services;
    }
}
