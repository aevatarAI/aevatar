using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public abstract class WorkflowModuleConfiguratorBase<TModule> : IWorkflowModuleConfigurator
    where TModule : class, IEventModule<IWorkflowExecutionContext>
{
    public virtual int Order => 0;

    public void Configure(IEventModule<IWorkflowExecutionContext> module, WorkflowDefinition workflow)
    {
        if (module is TModule typed)
            Configure(typed, workflow);
    }

    protected abstract void Configure(TModule module, WorkflowDefinition workflow);
}
