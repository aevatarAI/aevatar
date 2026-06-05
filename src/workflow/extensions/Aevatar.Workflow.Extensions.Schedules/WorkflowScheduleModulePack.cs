using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Extensions.Schedules.Modules;

namespace Aevatar.Workflow.Extensions.Schedules;

public sealed class WorkflowScheduleModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<WorkflowSelfRescheduleModule>("self_reschedule", "schedule_workflow"),
    ];

    public string Name => "workflow.extensions.schedules";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;

    public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => [];

    public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
}
