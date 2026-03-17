using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Workflow.Projection.Projectors;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowRunInsightReportGraphMaterializer
    : IProjectionGraphMaterializer<WorkflowRunInsightReportDocument>
{
    private static readonly WorkflowRunGraphArtifactMaterializer Inner = new();

    public ProjectionGraphMaterialization Materialize(WorkflowRunInsightReportDocument readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        return Inner.Materialize(WorkflowExecutionArtifactMaterializationSupport.BuildGraphDocument(readModel));
    }
}
