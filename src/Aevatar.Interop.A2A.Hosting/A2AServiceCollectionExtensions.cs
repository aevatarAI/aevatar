using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services required by the A2A protocol adapter layer.
    /// Prerequisite: the host must have already registered <c>IActorDispatchPort</c> (provided by the Foundation Runtime).
    /// </summary>
    public static IServiceCollection AddA2AAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton<IA2ATaskStore, InMemoryA2ATaskStore>();
        services.TryAddScoped<IA2AAdapterService, A2AAdapterService>();
        return services;
    }
}
