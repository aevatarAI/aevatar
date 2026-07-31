using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Infrastructure.ToolExecution;

public static class AgentToolAdmissionServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryAgentToolAdmissionLedger(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<
            IAgentToolAdmissionLedger,
            InMemoryAgentToolAdmissionLedger>());
        return services;
    }

    public static IServiceCollection AddGarnetAgentToolAdmissionLedger(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentToolAdmissionFactStore, GarnetAgentToolAdmissionFactStore>();
        services.Replace(ServiceDescriptor.Singleton<
            IAgentToolAdmissionLedger,
            DistributedAgentToolAdmissionLedger>());
        return services;
    }
}
