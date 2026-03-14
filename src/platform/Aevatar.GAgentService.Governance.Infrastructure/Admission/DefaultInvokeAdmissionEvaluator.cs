using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Infrastructure.Admission;

public sealed class DefaultInvokeAdmissionEvaluator : IInvokeAdmissionEvaluator
{
    public Task<InvokeAdmissionDecision> EvaluateAsync(
        InvokeAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);
        ct.ThrowIfCancellationRequested();

        var decision = new InvokeAdmissionDecision
        {
            Allowed = true,
        };
        foreach (var missingPolicyId in request.MissingPolicyIds)
        {
            decision.Allowed = false;
            decision.Violations.Add(new AdmissionViolation
            {
                Code = "missing_policy",
                SubjectId = missingPolicyId ?? string.Empty,
                Message = "Referenced policy was not found.",
            });
        }

        if (request.Endpoint.ExposureKind == ServiceEndpointExposureKind.Disabled)
        {
            decision.Allowed = false;
            decision.Violations.Add(new AdmissionViolation
            {
                Code = "endpoint_disabled",
                SubjectId = request.Endpoint.EndpointId ?? string.Empty,
                Message = "Endpoint exposure is disabled.",
            });
        }

        foreach (var policy in request.Policies)
        {
            if (policy.InvokeRequiresActiveDeployment && !request.HasActiveDeployment)
            {
                decision.Allowed = false;
                decision.Violations.Add(new AdmissionViolation
                {
                    Code = "inactive_deployment",
                    SubjectId = policy.PolicyId ?? string.Empty,
                    Message = "Invoke requires an active deployment.",
                });
            }

            if (policy.InvokeAllowedCallerServiceKeys.Count == 0)
                continue;

            var callerServiceKey = request.Caller?.ServiceKey ?? string.Empty;
            if (policy.InvokeAllowedCallerServiceKeys.Contains(callerServiceKey))
                continue;

            decision.Allowed = false;
            decision.Violations.Add(new AdmissionViolation
            {
                Code = "caller_not_allowed",
                SubjectId = policy.PolicyId ?? string.Empty,
                Message = "Caller service is not allowed by policy.",
            });
        }

        return Task.FromResult(decision);
    }
}
