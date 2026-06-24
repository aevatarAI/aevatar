using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel-free workflow scheduling tool
    /// (<c>aevatar_provision_workflow_schedule</c>) as an <see cref="IAgentToolSource"/>.
    /// The tool resolves the narrow <c>IWorkflowScheduleProvisioningPort</c> (registered
    /// by Studio's <c>AddStudioApplication</c>); the host composes both. The tool joins
    /// the host tool catalog; W2 scopes it to studio runs via the studio workflow's
    /// <c>allowed_tools</c> allowlist.
    /// </summary>
    public static IServiceCollection AddStudioProvisioningTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ProvisionWorkflowScheduleToolSource>());
        return services;
    }
}
