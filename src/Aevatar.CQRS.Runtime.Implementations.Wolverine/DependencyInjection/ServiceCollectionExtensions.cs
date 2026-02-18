using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Implementations.Wolverine.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsRuntimeWolverine(this IServiceCollection services)
    {
        services.TryAddSingleton<WolverineCommandBus>();
        services.TryAddSingleton<ICommandBus>(sp => sp.GetRequiredService<WolverineCommandBus>());
        services.TryAddSingleton<ICommandScheduler>(sp => sp.GetRequiredService<WolverineCommandBus>());
        return services;
    }
}
