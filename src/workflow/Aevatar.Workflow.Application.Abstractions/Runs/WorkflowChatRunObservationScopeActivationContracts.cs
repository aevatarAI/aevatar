namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatRunObservationScopeActivation(
    string ActorId,
    string CommandId);

public interface IWorkflowChatRunObservationScopeActivationPort
{
    Task<WorkflowChatRunObservationScopeActivation?> ActivateAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        WorkflowChatRunObservationScopeActivation activation,
        CancellationToken ct = default);
}
