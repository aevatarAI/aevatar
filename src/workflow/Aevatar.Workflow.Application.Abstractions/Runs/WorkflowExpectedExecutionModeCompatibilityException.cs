using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed class WorkflowExpectedExecutionModeCompatibilityException : InvalidOperationException
{
    public WorkflowExpectedExecutionModeCompatibilityException(
        string actorId,
        ExternalCapabilityExecutionMode persistedMode,
        ExternalCapabilityExecutionMode requestedMode)
        : base(
            $"Workflow definition actor '{actorId}' expected execution mode does not match the requested definition.")
    {
        ActorId = actorId;
        PersistedMode = persistedMode;
        RequestedMode = requestedMode;
    }

    public string ActorId { get; }

    public ExternalCapabilityExecutionMode PersistedMode { get; }

    public ExternalCapabilityExecutionMode RequestedMode { get; }
}
