namespace Aevatar.Workflow.Projection;

public interface IWorkflowExecutionRunIdResolver
{
    int Order { get; }

    bool TryResolve(EventEnvelope envelope, out string? runId);
}
