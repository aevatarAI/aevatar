using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.Tests;

internal static class ChannelWorkflowResultDeliveryCredentialTestData
{
    public static ChannelWorkflowResultDeliveryCredential Create(string suffix) =>
        new()
        {
            SecretReference = new SecretReference
            {
                Ref = $"sec_{suffix}",
                Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                OwnerScopeKey = "scope-owner",
            },
            SubjectId = $"api-key-{suffix}",
        };
}
