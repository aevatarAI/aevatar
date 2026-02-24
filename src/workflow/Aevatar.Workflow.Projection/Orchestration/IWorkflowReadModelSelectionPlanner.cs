using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public readonly record struct WorkflowReadModelSelectionPlan(
    ProjectionReadModelRequirements ReadModelRequirements,
    ProjectionReadModelStoreSelectionOptions ReadModelSelectionOptions,
    ProjectionReadModelRequirements RelationRequirements,
    ProjectionReadModelStoreSelectionOptions RelationSelectionOptions);

public interface IWorkflowReadModelSelectionPlanner
{
    WorkflowReadModelSelectionPlan Build(WorkflowExecutionProjectionOptions options);
}
