using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Core.DependencyInjection;

public static class AgentToolExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddAgentToolExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAgentToolAdmissionLedger, UnavailableAgentToolAdmissionLedger>();
        services.TryAddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>();
        return services;
    }
}
