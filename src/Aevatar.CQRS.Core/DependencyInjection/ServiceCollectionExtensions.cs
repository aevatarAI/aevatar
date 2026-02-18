using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Profiles;
using Aevatar.CQRS.Core.Streaming;

namespace Aevatar.CQRS.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsCore(
        this IServiceCollection services,
        Action<SubsystemSelectionOptions>? configure = null)
    {
        var options = new SubsystemSelectionOptions();
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<SubsystemSelectionOptions>(sp =>
            sp.GetRequiredService<SubsystemSelectionOptions>());
        services.TryAddSingleton<ICommandCorrelationPolicy, DefaultCommandCorrelationPolicy>();
        services.TryAddSingleton<ISubsystemProfileRegistry, DefaultSubsystemProfileRegistry>();
        services.TryAddTransient(typeof(IEventOutputStream<,>), typeof(DefaultEventOutputStream<,>));

        return services;
    }

    public static IServiceCollection AddSubsystemProfile<TProfile>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TProfile : class, ISubsystemProfile, new()
    {
        var profile = new TProfile();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISubsystemProfile>(profile));
        profile.Register(services, configuration);
        return services;
    }

    public static IServiceCollection AddSubsystemProfile(
        this IServiceCollection services,
        IConfiguration configuration,
        ISubsystemProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        services.TryAddEnumerable(ServiceDescriptor.Singleton(profile));
        profile.Register(services, configuration);
        return services;
    }
}
