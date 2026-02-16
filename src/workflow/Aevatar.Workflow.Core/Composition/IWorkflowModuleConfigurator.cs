using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public interface IWorkflowModuleConfigurator
{
    int Order { get; }

    void Configure(IEventModule module, WorkflowDefinition workflow);
}
