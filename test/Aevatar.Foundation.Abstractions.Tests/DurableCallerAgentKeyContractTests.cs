using Aevatar.Foundation.Abstractions.Credentials;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class DurableCallerAgentKeyContractTests
{
    [Theory]
    [InlineData(
        DurableCallerCredentialSourceKind.ChannelRegistration,
        CredentialSecretPurposes.ChannelNyxIdAgentKey)]
    [InlineData(
        DurableCallerCredentialSourceKind.WebhookBinding,
        CredentialSecretPurposes.WorkflowWebhookBindingAgentKey)]
    [InlineData(
        DurableCallerCredentialSourceKind.ScheduledDispatch,
        CredentialSecretPurposes.ScheduledInvocationAgentKey)]
    public void Matches_ShouldAcceptOnlyExactSourceAndPurposePairs(
        DurableCallerCredentialSourceKind sourceKind,
        string purpose)
    {
        DurableCallerAgentKeyContract.Matches(sourceKind, purpose).ShouldBeTrue();
        DurableCallerAgentKeyContract.Matches(
            sourceKind,
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken).ShouldBeFalse();
    }

    [Fact]
    public void Matches_ShouldRejectCrossedSourceAndPurpose()
    {
        DurableCallerAgentKeyContract.Matches(
            DurableCallerCredentialSourceKind.WebhookBinding,
            CredentialSecretPurposes.ChannelNyxIdAgentKey).ShouldBeFalse();
        DurableCallerAgentKeyContract.Matches(null).ShouldBeFalse();
    }

    [Fact]
    public void Matches_ShouldRequireProviderCredentialIdentityForWebhookReference()
    {
        var credential = new DurableCallerCredentialRef
        {
            SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
            Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
        };

        DurableCallerAgentKeyContract.Matches(credential).ShouldBeFalse();

        credential.ProviderCredentialId = "provider-key-1";
        DurableCallerAgentKeyContract.Matches(credential).ShouldBeTrue();
    }
}
