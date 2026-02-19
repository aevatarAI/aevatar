using Aevatar.Maker.Application.DependencyInjection;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Core;
using Aevatar.Maker.Infrastructure.Runs;
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
        services.AddSingleton<IMakerRunActorAdapter, WorkflowMakerRunActorAdapter>();
        services.AddMakerApplication();
        return services;
    }

    public static IServiceCollection AddMakerCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        return services.AddMakerInfrastructure();
    }
}
