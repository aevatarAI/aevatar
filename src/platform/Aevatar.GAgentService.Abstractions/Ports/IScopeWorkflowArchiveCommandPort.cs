namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowArchiveCommandPort
{
    Task<ScopeWorkflowArchiveAcceptedResult> ArchiveAsync(
        ScopeWorkflowArchiveRequest request,
        CancellationToken ct = default);
}
