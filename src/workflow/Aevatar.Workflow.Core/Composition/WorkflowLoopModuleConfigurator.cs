using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowLoopModuleConfigurator : WorkflowModuleConfiguratorBase<WorkflowLoopModule>
{
    public override int Order => 0;

    protected override void Configure(WorkflowLoopModule module, WorkflowDefinition workflow)
    {
        module.SetWorkflow(workflow);
    }
}
