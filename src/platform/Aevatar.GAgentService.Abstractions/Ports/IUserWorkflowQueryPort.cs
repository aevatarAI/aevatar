namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IUserWorkflowQueryPort
{
    Task<IReadOnlyList<UserWorkflowSummary>> ListAsync(
        string userId,
        CancellationToken ct = default);

    Task<UserWorkflowSummary?> GetByWorkflowIdAsync(
        string userId,
        string workflowId,
        CancellationToken ct = default);

    Task<UserWorkflowSummary?> GetByActorIdAsync(
        string userId,
        string actorId,
        CancellationToken ct = default);
}
