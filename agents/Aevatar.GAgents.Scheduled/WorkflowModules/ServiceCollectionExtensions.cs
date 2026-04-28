using Aevatar.Workflow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Scheduled.WorkflowModules;

/// <summary>
/// DI extension to register the scheduled-agent workflow module pack. Hosts that compose
/// the social_media template's execution should call this so the <c>twitter_publish</c>
/// step type resolves at workflow run time.
/// </summary>
public static class ScheduledWorkflowModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ScheduledWorkflowModulePack"/> alongside any other module
    /// packs already added to the workflow runtime. Idempotent — uses
    /// <c>TryAddEnumerable</c> via
    /// <see cref="ServiceCollectionExtensions.AddWorkflowModulePack{TModulePack}"/>.
    /// </summary>
    public static IServiceCollection AddScheduledWorkflowExtensions(this IServiceCollection services) =>
        services.AddWorkflowModulePack<ScheduledWorkflowModulePack>();
}
