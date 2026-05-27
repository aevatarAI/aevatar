using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: workflow AI behavior was implicit in core module composition. New principle: Integration.AI contributes its adapter module through an explicit module pack.
public sealed class WorkflowAiModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanderRegistrations =
    [
        new WorkflowAiModuleDependencyExpander(),
    ];

    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<WorkflowAiMessageAdapterModule>("workflow_ai_message_adapter"),
    ];

    public string Name => "workflow.integration.ai";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;

    public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => DependencyExpanderRegistrations;

    public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
}
