using Aevatar.Workflow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.WorkflowModules;

/// <summary>
/// DI extension to register the ChannelRuntime workflow module pack. Hosts that compose
/// social_media template execution should call this so the <c>twitter_publish</c> step
/// type resolves at workflow run time.
/// </summary>
public static class ChannelRuntimeWorkflowModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ChannelRuntimeWorkflowModulePack"/> alongside any other module
    /// packs already added to the workflow runtime. Idempotent — uses
    /// <c>TryAddEnumerable</c> via <see cref="ServiceCollectionExtensions.AddWorkflowModulePack{TModulePack}"/>.
    /// </summary>
    public static IServiceCollection AddChannelRuntimeWorkflowExtensions(this IServiceCollection services) =>
        services.AddWorkflowModulePack<ChannelRuntimeWorkflowModulePack>();
}
