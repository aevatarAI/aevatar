using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Maker.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMakerApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IMakerRunActorAdapter, UnconfiguredMakerRunActorAdapter>();
        services.AddSingleton<IMakerRunApplicationService, MakerRunApplicationService>();
        return services;
    }
}
