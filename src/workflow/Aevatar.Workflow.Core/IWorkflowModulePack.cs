using Aevatar.Workflow.Core.Composition;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Unified contribution contract for workflow modules.
/// Both built-in workflow modules and extension modules use the same pack model.
/// </summary>
public interface IWorkflowModulePack
{
    string Name { get; }

    IReadOnlyList<WorkflowModuleRegistration> Modules { get; }

    IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; }

    IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; }
}
