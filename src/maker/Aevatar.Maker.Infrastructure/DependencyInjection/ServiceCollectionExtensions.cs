using Aevatar.Maker.Application.DependencyInjection;
using Aevatar.Maker.Core;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Maker.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMakerInfrastructure(this IServiceCollection services)
    {
        services.AddAevatarWorkflow();
        services.AddAevatarMakerCore();
        services.AddMakerApplication();
        return services;
    }

    public static IServiceCollection AddMakerSubsystem(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        return services.AddMakerInfrastructure();
    }
}
