using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IActivationAdmissionEvaluator
{
    Task<ActivationAdmissionDecision> EvaluateAsync(
        ActivationAdmissionRequest request,
        CancellationToken ct = default);
}
