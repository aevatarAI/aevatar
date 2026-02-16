using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public abstract class WorkflowModuleConfiguratorBase<TModule> : IWorkflowModuleConfigurator
    where TModule : class, IEventModule
{
    public virtual int Order => 0;

    public void Configure(IEventModule module, WorkflowDefinition workflow)
    {
        if (module is TModule typed)
            Configure(typed, workflow);
    }

    protected abstract void Configure(TModule module, WorkflowDefinition workflow);
}
