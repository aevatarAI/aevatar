using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WhileModuleConfigurator : WorkflowModuleConfiguratorBase<WhileModule>
{
    public override int Order => 0;

    protected override void Configure(WhileModule module, WorkflowDefinition workflow)
    {
        module.SetWorkflow(workflow);
    }
}
