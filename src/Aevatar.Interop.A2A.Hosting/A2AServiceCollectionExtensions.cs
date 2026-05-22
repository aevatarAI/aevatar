using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services required by the A2A protocol adapter layer.
    /// Prerequisites: the host must have registered actor runtime/dispatch, projection reader, and event subscription provider.
    /// </summary>
    // Refactor (iter30/cluster-031-a2a-actor-owned):
    //   Old pattern: hosting could opt into process-local IA2ATaskStore lifecycle facts.
    //   New principle: adapter uses task command/readmodel/subscription ports; lifecycle belongs to task GAgent.
    public static IServiceCollection AddA2AAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.AddCurrentStateProjectionMaterializer<
            A2ATaskProjectionContext,
            A2ATaskCurrentStateProjector>();
        services.TryAddScoped<IA2ATaskCommandPort, A2ATaskCommandPort>();
        services.TryAddScoped<IA2AAdapterService, A2AAdapterService>();
        return services;
    }
}
