using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;

namespace Aevatar.GAgents.ChannelRuntime.WorkflowModules;

/// <summary>
/// Workflow module pack contributed by ChannelRuntime — currently registers
/// <see cref="TwitterPublishModule"/> for the social_media template's
/// <c>twitter_publish</c> step (issue aevatarAI/aevatar#216). Lives next to its
/// dependencies (<c>NyxIdApiClient</c>, <see cref="ChannelMetadataKeys"/>,
/// <see cref="LarkProxyResponse"/>) instead of in <c>Aevatar.Workflow.Core</c> so the
/// generic workflow runtime stays free of channel-specific compile-time coupling.
/// </summary>
public sealed class ChannelRuntimeWorkflowModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<TwitterPublishModule>("twitter_publish"),
    ];

    public string Name => "channelruntime.workflow";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;

    public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => [];

    public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
}
