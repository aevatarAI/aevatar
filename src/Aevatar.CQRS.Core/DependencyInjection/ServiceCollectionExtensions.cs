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
        services.TryAddSingleton(typeof(IActorOutcomeChannel<>), typeof(StreamActorOutcomeChannel<>));
        services.TryAddSingleton(typeof(ICommandObservationLifecycle<,,,>), typeof(NoOpCommandObservationLifecycle<,,,>));
        services.TryAddTransient(typeof(IEventOutputStream<,>), typeof(DefaultEventOutputStream<,>));

        return services;
    }
}
