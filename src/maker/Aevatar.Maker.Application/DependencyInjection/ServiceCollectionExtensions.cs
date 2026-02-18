using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Application.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Maker.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMakerApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMakerRunApplicationService, MakerRunApplicationService>();
        return services;
    }
}
