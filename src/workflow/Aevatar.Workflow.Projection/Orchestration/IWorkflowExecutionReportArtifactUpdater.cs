namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowExecutionReportArtifactUpdater
{
    Task RefreshMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default);

    Task MarkStoppedAsync(
        string actorId,
        CancellationToken ct = default);
}
