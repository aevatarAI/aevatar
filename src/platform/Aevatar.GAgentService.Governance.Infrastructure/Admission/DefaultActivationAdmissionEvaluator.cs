using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Infrastructure.Admission;

public sealed class DefaultActivationAdmissionEvaluator : IActivationAdmissionEvaluator
{
    public Task<ActivationAdmissionDecision> EvaluateAsync(
        ActivationAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CapabilityView);
        ct.ThrowIfCancellationRequested();

        var decision = new ActivationAdmissionDecision
        {
            Allowed = true,
        };
        var view = request.CapabilityView;
        foreach (var missingPolicyId in view.MissingPolicyIds)
        {
            decision.Allowed = false;
            decision.Violations.Add(new AdmissionViolation
            {
                Code = "missing_policy",
                SubjectId = missingPolicyId ?? string.Empty,
                Message = "Referenced policy was not found.",
            });
        }

        var activeBindingIds = view.Bindings
            .Select(x => x.BindingId ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var policy in view.Policies)
        {
            foreach (var requiredBindingId in policy.ActivationRequiredBindingIds)
            {
                if (activeBindingIds.Contains(requiredBindingId))
                    continue;

                decision.Allowed = false;
                decision.Violations.Add(new AdmissionViolation
                {
                    Code = "missing_binding",
                    SubjectId = requiredBindingId ?? string.Empty,
                    Message = $"Activation requires binding '{requiredBindingId}'.",
                });
            }
        }

        return Task.FromResult(decision);
    }
}
