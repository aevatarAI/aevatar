namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Read-only view of the LLM preferences NyxID-bound users carry across
/// chat surfaces. Reads are projection-backed (additive only — failures fall
/// back to provider defaults). The optional <paramref name="senderBindingId"/>
/// argument scopes the lookup to a specific binding (issue #513 phase 2):
/// pass the inbound sender's NyxID binding-id to read sender-specific prefs;
/// pass <c>null</c> (or omit) to read the ambient/bot-owner prefs the way
/// non-channel callers (Studio API, streaming proxy) always have.
/// </summary>
public interface INyxIdUserLlmPreferencesStore
{
    /// <summary>
    /// Read LLM preferences for the requested binding, or for the ambient
    /// scope when <paramref name="senderBindingId"/> is null/empty. Returns
    /// a record with empty <c>DefaultModel</c> / <c>PreferredRoute</c> when
    /// no document exists for the requested scope so callers can apply a
    /// downstream override chain (sender → owner → provider default).
    /// </summary>
    Task<NyxIdUserLlmPreferences> GetAsync(string? senderBindingId, CancellationToken cancellationToken = default);
}

public sealed record NyxIdUserLlmPreferences(
    string DefaultModel,
    string PreferredRoute,
    int MaxToolRounds = 0);
