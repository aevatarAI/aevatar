using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class DefaultScheduledDispatchCredentialRequirementPolicy
    : IScheduledDispatchCredentialRequirementPolicy
{
    public static DefaultScheduledDispatchCredentialRequirementPolicy Instance { get; } = new();

    public ScheduledDispatchCredentialRequirementDecision Evaluate(
        ScheduledDispatchCredentialRequirementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentialRequired = RequiresCredential(request.TargetKind);
        if (request.PayloadCredentialSignal.HasCurrentSessionCredential)
        {
            return ScheduledDispatchCredentialRequirementDecision.Deny(
                credentialRequired,
                ScheduledDispatchCredentialViolationCode.CurrentSessionCredential,
                $"Scheduled dispatch cannot persist current-session credentials from {request.PayloadCredentialSignal.Source}; configure a typed service invocation credential source instead.");
        }

        return request.CredentialSource.Kind switch
        {
            ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer =>
                ScheduledDispatchCredentialRequirementDecision.Deny(
                    credentialRequired,
                    ScheduledDispatchCredentialViolationCode.UnsupportedCredentialSource,
                    "Durable sender bearer token schedule auth is no longer supported; use senderNyxId or scopeOwnerNyxId."),
            ScheduledDispatchCredentialSourceKind.Multiple =>
                ScheduledDispatchCredentialRequirementDecision.Deny(
                    credentialRequired,
                    ScheduledDispatchCredentialViolationCode.InvalidCredentialSource,
                    "Exactly one service invocation credential source is required."),
            ScheduledDispatchCredentialSourceKind.None
                when credentialRequired =>
                    ScheduledDispatchCredentialRequirementDecision.Deny(
                        credentialRequired,
                        ScheduledDispatchCredentialViolationCode.CredentialRequired,
                        $"Scheduled dispatch target '{request.TargetKind}' requires a typed service invocation credential source."),
            _ => ScheduledDispatchCredentialRequirementDecision.Allow(credentialRequired),
        };
    }

    private static bool RequiresCredential(ScheduledDispatchCredentialRequirementTargetKind targetKind) =>
        targetKind is ScheduledDispatchCredentialRequirementTargetKind.WorkflowService
            or ScheduledDispatchCredentialRequirementTargetKind.Connector;
}
