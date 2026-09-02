using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelWorkflowResultDeliveryCapability
{
    public static ChannelWorkflowResultDeliveryCapabilityStatus Resolve(
        ChannelBotRegistrationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
        var reference = entry.WorkflowResultDeliveryCredential;
        return !string.IsNullOrWhiteSpace(entry.NyxAgentApiKeyId) &&
               reference is not null &&
               !string.IsNullOrWhiteSpace(reference.Ref) &&
               string.Equals(
                   reference.Purpose,
                   CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                   StringComparison.Ordinal) &&
               string.Equals(reference.OwnerScopeKey, entry.ScopeId, StringComparison.Ordinal);
    }
}
