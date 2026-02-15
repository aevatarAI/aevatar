using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public interface IWorkflowModuleDependencyExpander
{
    int Order { get; }

    void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames);
}
