namespace Aevatar.Workflow.Projection.Stores;

public sealed class WorkflowExecutionReadModelNotFoundException : KeyNotFoundException
{
    public string ActorId { get; }

    public WorkflowExecutionReadModelNotFoundException(string actorId)
        : base($"Workflow actor read model not found: '{actorId}'.")
    {
        ActorId = actorId;
    }
}
