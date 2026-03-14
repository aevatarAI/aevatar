using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IInvokeAdmissionEvaluator
{
    Task<InvokeAdmissionDecision> EvaluateAsync(
        InvokeAdmissionRequest request,
        CancellationToken ct = default);
}
