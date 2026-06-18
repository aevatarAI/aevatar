namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Resolves the realtime provider API credential at connect time instead of
/// reading a static key from configuration. Lets a deployment broker a
/// short-lived provider secret (e.g. an OpenAI Realtime ephemeral client
/// secret minted through NyxID) per session, so no long-lived provider key is
/// held by aevatar grain/config state (zero-secret-material; see ADR-0018 and
/// ADR-0033).
/// </summary>
// Refactor (cluster-voice-nyxid-ephemeral-broker):
//   Old pattern: VoiceProviderConfig.ApiKey carried a static OPENAI_API_KEY loaded from host config/env;
//     IsOpenAIVoiceConfigured gated registration on that static key being present.
//   New principle: the long-lived provider key lives only in the credential broker (NyxID); the realtime
//     provider credential is resolved per connect into a short-lived ephemeral, and aevatar connects
//     directly to the provider with it. NyxID is never in the audio hot path (ADR-013).
public interface IRealtimeProviderCredentialResolver
{
    /// <summary>
    /// Returns the effective provider API key for this session, or <c>null</c>
    /// to fall back to <see cref="VoiceProviderConfig.ApiKey" /> (static-key /
    /// local-dev path). The returned key is short-lived and used only to open
    /// the provider connection; it is never persisted.
    /// </summary>
    /// <exception cref="RealtimeProviderCredentialException">
    /// Thrown when brokering was attempted (a caller credential was present)
    /// but minting failed. Broker mode does not silently fall back to a
    /// missing static key.
    /// </exception>
    Task<string?> ResolveApiKeyAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        CancellationToken ct);
}

/// <summary>Raised when realtime provider credential brokering was attempted and failed.</summary>
public sealed class RealtimeProviderCredentialException : Exception
{
    public RealtimeProviderCredentialException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
