using Aevatar.AI.Abstractions.CodexExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddManagedCodexApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ManagedCodexOptions>()
            .Bind(configuration.GetSection(ManagedCodexOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ManagedCodexOptions>,
            ManagedCodexOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IManagedCodexCredentialLifecycle, ManagedCodexCredentialLifecycle>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICodexExecutionPort,
            ManagedCodexExecutionCoordinator>());
        return services;
    }
}
