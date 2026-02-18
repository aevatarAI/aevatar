using Aevatar.CQRS.Core.Abstractions.Profiles;
using Aevatar.Maker.Application.DependencyInjection;
using Aevatar.Maker.Core;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    public static IServiceCollection AddMakerSubsystemProfile(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var profile = new MakerSubsystemProfile();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISubsystemProfile>(profile));
        profile.Register(services, configuration);
        return services;
    }
}

public sealed class MakerSubsystemProfile : ISubsystemProfile
{
    public string Name => "maker";

    public IServiceCollection Register(IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.AddMakerInfrastructure();
        return services;
    }
}
