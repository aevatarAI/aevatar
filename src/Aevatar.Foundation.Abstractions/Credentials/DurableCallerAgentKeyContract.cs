namespace Aevatar.Foundation.Abstractions.Credentials;

public static class DurableCallerAgentKeyContract
{
    public static bool Matches(DurableCallerCredentialRef? credential) =>
        credential is not null &&
        Matches(credential.SourceKind, credential.Purpose) &&
        (credential.SourceKind != DurableCallerCredentialSourceKind.WebhookBinding ||
         !string.IsNullOrWhiteSpace(credential.ProviderCredentialId));

    public static bool Matches(
        DurableCallerCredentialSourceKind sourceKind,
        string? purpose) =>
        sourceKind switch
        {
            DurableCallerCredentialSourceKind.ChannelRegistration =>
                string.Equals(
                    purpose,
                    CredentialSecretPurposes.ChannelNyxIdAgentKey,
                    StringComparison.Ordinal),
            DurableCallerCredentialSourceKind.WebhookBinding =>
                string.Equals(
                    purpose,
                    CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
                    StringComparison.Ordinal),
            DurableCallerCredentialSourceKind.ScheduledDispatch =>
                string.Equals(
                    purpose,
                    CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    StringComparison.Ordinal),
            _ => false,
        };
}
