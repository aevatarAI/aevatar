using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowTextInvocationPort
{
    Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ScopeWorkflowTextInvocationRequest request,
        WorkflowCallerCredential? callerCredential,
        CancellationToken ct = default);
}
