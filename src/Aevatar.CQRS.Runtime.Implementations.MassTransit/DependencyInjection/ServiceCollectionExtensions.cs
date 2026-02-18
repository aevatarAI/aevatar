using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Implementations.MassTransit.Commands;
using Aevatar.CQRS.Runtime.Implementations.MassTransit.Consumers;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Runtime.Implementations.MassTransit.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsRuntimeMassTransit(this IServiceCollection services)
    {
        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<QueuedCommandConsumer>();
            configurator.UsingInMemory((context, cfg) =>
            {
                cfg.ConfigureEndpoints(context);
            });
        });

        services.TryAddSingleton<MassTransitCommandBus>();
        services.TryAddSingleton<ICommandBus>(sp => sp.GetRequiredService<MassTransitCommandBus>());
        services.TryAddSingleton<ICommandScheduler>(sp => sp.GetRequiredService<MassTransitCommandBus>());
        return services;
    }
}
