using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.Application.CodexExecution.DependencyInjection;
using Aevatar.AI.Abstractions.CodeExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChronoSandboxCodexExecution(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useInMemoryMutationLease)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddChronoSandboxCodeExecution();
        services.AddManagedCodexApplication(configuration);
        services.TryAddSingleton<IManagedCodexNyxIdCredentialPort, NyxIdManagedCodexCredentialAdapter>();
        if (useInMemoryMutationLease)
        {
            services.TryAddSingleton<
                IManagedCodexCredentialMutationLease,
                InMemoryManagedCodexCredentialMutationLease>();
        }
        else
        {
            services.TryAddSingleton<
                IManagedCodexCredentialMutationLease,
                GarnetManagedCodexCredentialMutationLease>();
        }
        services.TryAddSingleton<
            IManagedCodexChronoTransport,
            NyxIdManagedCodexChronoTransport>();
        return services;
    }

    public static IServiceCollection AddChronoSandboxCodeExecution(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICodeExecutionPort, NyxIdCodeExecutionPort>();
        return services;
    }
}
