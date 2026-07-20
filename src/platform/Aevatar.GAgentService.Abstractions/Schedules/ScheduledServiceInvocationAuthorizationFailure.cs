namespace Aevatar.GAgentService.Abstractions.Schedules;

public enum ScheduledServiceInvocationAuthorizationFailureCode
{
    CredentialExpired = 1,
    CredentialReferenceMissing = 2,
    CredentialReferenceInvalid = 3,
    ApiKeyIdMissing = 4,
    CredentialUnresolvable = 5,
    CredentialVaultUnavailable = 6,
    AuthorizationFactInvalid = 7,
}

public sealed class ScheduledServiceInvocationAuthorizationException : InvalidOperationException
{
    public ScheduledServiceInvocationAuthorizationException(
        ScheduledServiceInvocationAuthorizationFailureCode code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public ScheduledServiceInvocationAuthorizationFailureCode Code { get; }

    public string StableCode => Code switch
    {
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialExpired => "credential_expired",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceMissing =>
            "credential_reference_missing",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid =>
            "credential_reference_invalid",
        ScheduledServiceInvocationAuthorizationFailureCode.ApiKeyIdMissing => "api_key_id_missing",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialUnresolvable =>
            "credential_unresolvable",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable =>
            "credential_vault_unavailable",
        ScheduledServiceInvocationAuthorizationFailureCode.AuthorizationFactInvalid =>
            "authorization_fact_invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(Code), Code, "Unsupported authorization failure code."),
    };
}
