namespace Aevatar.CQRS.Projections.Stores;

public sealed class WorkflowExecutionReadModelNotFoundException : KeyNotFoundException
{
    public string RunId { get; }

    public WorkflowExecutionReadModelNotFoundException(string runId)
        : base($"Chat run read model not found: '{runId}'.")
    {
        RunId = runId;
    }
}
