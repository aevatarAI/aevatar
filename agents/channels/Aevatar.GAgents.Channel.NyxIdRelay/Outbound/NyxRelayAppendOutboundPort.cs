using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>Credential and platform boundary for one Actor-owned append attempt.</summary>
internal sealed class NyxRelayAppendOutboundPort(
    NyxIdApiClient client,
    IChannelBotRegistrationRuntimeQueryPort registrations,
    IChannelBotRegistrationQueryByNyxIdentityPort registrationsByIdentity,
    ISecretVault vault,
    IEnumerable<IMessageComposer> composers) : INyxRelayAppendOutboundPort
{
    private readonly IMessageComposer[] _composers = composers.ToArray();

    public string PrepareText(string platform, ConversationReference conversation, string rawText)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(rawText);
        var normalizedPlatform = platform?.Trim();
        if (string.IsNullOrEmpty(normalizedPlatform))
            throw new InvalidOperationException("append_platform_required");

        try
        {
            var composer = _composers.SingleOrDefault(candidate =>
                string.Equals(candidate.Channel.Value, normalizedPlatform, StringComparison.OrdinalIgnoreCase));
            if (composer is ILosslessPlainTextFormatter formatter)
                return formatter.PreparePlainText(rawText, conversation);
            if (composer is not null)
                throw new InvalidOperationException("append_lossless_formatter_unavailable");

            return rawText;
        }
        catch (Exception)
        {
            // A formatter may include its source text in an exception. The Actor sees only
            // this controlled code, without the original exception as an inner exception.
            throw new InvalidOperationException("append_text_preparation_failed");
        }
    }

    public async Task<NyxRelayAppendSendResult> SendAsync(
        ChatActivity activity,
        string rawText,
        int maxLength,
        Func<CancellationToken, Task> onDispatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(onDispatch);
        try
        {
            var anchor = activity.OutboundDelivery?.ReplyMessageId;
            var platform = activity.TransportExtras?.NyxPlatform;
            if (string.IsNullOrWhiteSpace(platform))
                platform = activity.ChannelId?.Value;
            if (string.IsNullOrWhiteSpace(anchor) || string.IsNullOrWhiteSpace(platform) || maxLength <= 0)
                return PreDispatchFailure("append_target_invalid");

            var prepared = PrepareText(platform, activity.Conversation ?? new ConversationReference(), rawText);
            if (string.IsNullOrWhiteSpace(prepared))
                return PreDispatchFailure("append_empty_text");
            if (prepared.Length > maxLength)
                return PreDispatchFailure("append_segment_too_long");

            // Preserve the caller's Actor scheduler across both preparation awaits:
            // onDispatch commits the Actor-owned dispatch fence before HTTP starts.
            var registration = await ResolveRegistrationAsync(activity, ct);
            var credential = registration?.ChannelAgentKey;
            var reference = credential?.SecretReference;
            if (registration is null || !HasValidCredential(registration, credential, reference, platform))
                return PreDispatchFailure("append_agent_key_unavailable");

            var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
                reference!.Ref, reference.Purpose, reference.OwnerScopeKey, credential!.ApiKeyId,
                "channel-relay-append"), ct);
            if (!resolved.Resolved || string.IsNullOrWhiteSpace(resolved.Secret) || !reference.Equals(resolved.Reference))
                return PreDispatchFailure("append_agent_key_unavailable");

            var result = await client.SendChannelRelayAppendTextAsync(
                resolved.Secret, anchor, prepared, onDispatch, ct).ConfigureAwait(false);
            return new NyxRelayAppendSendResult(result.State switch
            {
                NyxIdChannelRelayAppendState.Accepted => NyxRelayAppendSendState.Accepted,
                NyxIdChannelRelayAppendState.Rejected => NyxRelayAppendSendState.Rejected,
                NyxIdChannelRelayAppendState.DeliveryUnknown => NyxRelayAppendSendState.DeliveryUnknown,
                _ => NyxRelayAppendSendState.PreDispatchFailure,
            }, result.PlatformMessageId, result.ErrorCode, result.HttpStatus);
        }
        catch (Exception)
        {
            // HTTP and dispatch-fence exceptions are already converted by the client. This
            // boundary catches only preparation/registration/Vault failures and exposes no raw details.
            return PreDispatchFailure("append_preparation_failed");
        }
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(ChatActivity activity, CancellationToken ct)
    {
        var keyId = activity.TransportExtras?.NyxAgentApiKeyId;
        var scopeId = activity.TransportExtras?.NyxRegistrationScopeId;
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            var candidates = await registrationsByIdentity.ListByNyxAgentApiKeyIdAsync(keyId, ct).ConfigureAwait(false);
            var active = candidates.Where(candidate => !candidate.Tombstoned).ToArray();
            return active.Length == 1 &&
                   (string.IsNullOrWhiteSpace(scopeId) || string.Equals(active[0].ScopeId, scopeId, StringComparison.Ordinal))
                ? active[0]
                : null;
        }

        if (string.IsNullOrWhiteSpace(activity.Bot?.Value))
            return null;
        var registration = await registrations.GetAsync(activity.Bot.Value, ct).ConfigureAwait(false);
        return registration is { Tombstoned: false } &&
               (string.IsNullOrWhiteSpace(scopeId) || string.Equals(registration.ScopeId, scopeId, StringComparison.Ordinal))
            ? registration
            : null;
    }

    private static bool HasValidCredential(ChannelBotRegistrationEntry registration,
        ChannelAgentKeyCredential? credential, SecretReference? reference, string platform) =>
        credential is not null && reference is not null &&
        !registration.Tombstoned && !string.IsNullOrWhiteSpace(registration.Id) &&
        !string.IsNullOrWhiteSpace(registration.ScopeId) && !string.IsNullOrWhiteSpace(credential.ApiKeyId) &&
        string.Equals(registration.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(registration.NyxAgentApiKeyId, credential.ApiKeyId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(reference.Purpose, CredentialSecretPurposes.ChannelNyxIdAgentKey, StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, registration.ScopeId, StringComparison.Ordinal) &&
        reference.Version > 0 && !string.IsNullOrWhiteSpace(reference.Fingerprint) && reference.CreatedAtUnixMs > 0 &&
        (reference.ExpiresAtUnixMs == 0 || reference.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static NyxRelayAppendSendResult PreDispatchFailure(string errorCode) =>
        new(NyxRelayAppendSendState.PreDispatchFailure, ErrorCode: errorCode);
}
