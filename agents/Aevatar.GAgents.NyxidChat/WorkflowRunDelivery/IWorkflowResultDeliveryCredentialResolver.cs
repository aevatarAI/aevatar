using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

/// <summary>
/// Narrow resolution port for the channel workflow terminal-result delivery agent key.
/// Resolves only vault handles minted for
/// <see cref="CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey"/> under the
/// handle's own owner scope and api-key subject; every other credential shape resolves to
/// <c>null</c> so delivery fails closed without an outbound call.
/// </summary>
public interface IWorkflowResultDeliveryCredentialResolver
{
    Task<string?> ResolveAsync(
        ChannelWorkflowResultDeliveryCredential credential,
        CancellationToken ct = default);
}

public sealed class SecretVaultWorkflowResultDeliveryCredentialResolver
    : IWorkflowResultDeliveryCredentialResolver
{
    private readonly ISecretVault _secretVault;

    public SecretVaultWorkflowResultDeliveryCredentialResolver(ISecretVault secretVault)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    }

    public async Task<string?> ResolveAsync(
        ChannelWorkflowResultDeliveryCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var secretReference = credential.SecretReference;
        if (string.IsNullOrWhiteSpace(secretReference?.Ref) ||
            string.IsNullOrWhiteSpace(credential.SubjectId) ||
            !string.Equals(
                secretReference.Purpose,
                CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                StringComparison.Ordinal))
        {
            return null;
        }

        var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    secretReference.Ref,
                    CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                    secretReference.OwnerScopeKey,
                    credential.SubjectId,
                    "workflow-run-delivery-terminal-reply"),
                ct)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved.Secret) ? null : resolved.Secret.Trim();
    }
}
