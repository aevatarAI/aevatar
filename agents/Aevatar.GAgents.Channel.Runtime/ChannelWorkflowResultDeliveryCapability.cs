using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelWorkflowResultDeliveryCapability
{
    public static ChannelWorkflowResultDeliveryCapabilityStatus Resolve(
        ChannelBotRegistrationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var contractKind = ChannelRegistrationAuthorizationContract.Classify(entry);
        if (contractKind is ChannelRegistrationAuthorizationContractKind.NyxIdDefault or
            ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist)
            return ChannelWorkflowResultDeliveryCapabilityStatus.Enabled;
        if (contractKind == ChannelRegistrationAuthorizationContractKind.Invalid)
            return ChannelWorkflowResultDeliveryCapabilityStatus.RepairRequired;

        return entry.WorkflowResultDeliveryRepair?.Status switch
        {
            ChannelWorkflowResultDeliveryRepairStatus.Failed =>
                ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed,
            ChannelWorkflowResultDeliveryRepairStatus.Requested or
                ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared =>
                ChannelWorkflowResultDeliveryCapabilityStatus.Repairing,
            _ when IsEnabled(entry) =>
                ChannelWorkflowResultDeliveryCapabilityStatus.Enabled,
            _ => ChannelWorkflowResultDeliveryCapabilityStatus.RepairRequired,
        };
    }

    public static bool IsEnabled(ChannelBotRegistrationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryGetDeliveryCredential(entry, out _, out _);
    }

    public static bool TryGetDeliveryCredential(
        ChannelBotRegistrationEntry entry,
        out string apiKeyId,
        out SecretReference? secretReference)
    {
        ArgumentNullException.ThrowIfNull(entry);

        switch (ChannelRegistrationAuthorizationContract.Classify(entry))
        {
            case ChannelRegistrationAuthorizationContractKind.NyxIdDefault:
            case ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist:
                apiKeyId = entry.ChannelAgentKey.ApiKeyId;
                secretReference = entry.ChannelAgentKey.SecretReference.Clone();
                return true;
            case ChannelRegistrationAuthorizationContractKind.HistoricalLegacy
                when IsLegacyCredentialUsable(entry):
                apiKeyId = entry.NyxAgentApiKeyId.Trim();
                secretReference = entry.WorkflowResultDeliveryCredential.Clone();
                return true;
            default:
                apiKeyId = string.Empty;
                secretReference = null;
                return false;
        }
    }

    private static bool IsLegacyCredentialUsable(ChannelBotRegistrationEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.NyxAgentApiKeyId) &&
        entry.WorkflowResultDeliveryCredential is { } reference &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, entry.ScopeId, StringComparison.Ordinal);
}
