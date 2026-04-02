using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>DI registration for NyxID tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NyxID tool system. When BaseUrl is configured, all NyxID management
    /// tools are automatically available to any AIGAgentBase-derived agent.
    /// Also registers <see cref="NyxIdToolApprovalHandler"/> as the
    /// <see cref="IToolApprovalHandler"/> so agents can route tool approvals
    /// through NyxID (Telegram / mobile app).
    /// </summary>
    public static IServiceCollection AddNyxIdTools(
        this IServiceCollection services,
        Action<NyxIdToolOptions> configure)
    {
        var options = new NyxIdToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, NyxIdAgentToolSource>());

        // Remote approval handler for timeout escalation (NyxID Telegram/app push).
        services.TryAddSingleton<IToolApprovalHandler>(sp =>
            new NyxIdToolApprovalHandler(sp.GetRequiredService<NyxIdApiClient>()));

        return services;
    }
}
