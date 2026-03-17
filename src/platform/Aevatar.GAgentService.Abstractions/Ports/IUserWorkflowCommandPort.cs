namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IUserWorkflowCommandPort
{
    Task<UserWorkflowUpsertResult> UpsertAsync(
        UserWorkflowUpsertRequest request,
        CancellationToken ct = default);
}
