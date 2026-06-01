using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;

namespace Aevatar.CQRS.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsCore(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommandContextPolicy, DefaultCommandContextPolicy>();
        // Refactor (iter158/cluster-001-stream-actor-outcome-rpc):
        // Old: outcome dispatch services were registered to use stream subscribe + TCS as a "first outcome" RPC reply, violating stream request-reply boundaries and honest ACK semantics.
        // New: Delete that abstraction with no replacement; callers use accepted receipts plus readmodel queries or typed continuation events.
        services.TryAddSingleton(typeof(ICommandObservationLifecycle<,,,>), typeof(NoOpCommandObservationLifecycle<,,,>));
        services.TryAddTransient(typeof(IEventOutputStream<,>), typeof(DefaultEventOutputStream<,>));

        return services;
    }
}
