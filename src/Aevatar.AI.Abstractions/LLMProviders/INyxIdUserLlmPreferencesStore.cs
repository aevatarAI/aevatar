namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Read-only view of the LLM preferences NyxID-bound users carry across
/// chat surfaces. Reads are projection-backed (additive only — failures fall
/// back to provider defaults).
/// </summary>
/// <remarks>
/// The two methods are deliberately distinct so call sites have to commit
/// to a scope at the type level (issue #513 phase 2 follow-up). The earlier
/// shape <c>GetAsync(string? senderBindingId)</c> let any caller drop into
/// the bot-owner ambient scope by passing <c>null</c>, which made it easy
/// for a future caller to leak owner-scoped config when they meant to read
/// a sender's prefs.
/// </remarks>
public interface INyxIdUserLlmPreferencesStore
{
    /// <summary>
    /// Read prefs for the ambient (bot-owner) scope. Used by Studio API and
    /// the streaming proxy where there is no inbound sender — every caller
    /// of this method intends to read the bot owner's pinned config.
    /// </summary>
    Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read prefs for a specific NyxID binding-id. Returns a record with
    /// empty <c>DefaultModel</c> / <c>PreferredRoute</c> when the sender
    /// has not set their own values, so callers can layer this on top of
    /// the bot-owner record (sender → owner → provider default).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="bindingId"/> is null or whitespace —
    /// callers must use <see cref="GetOwnerAsync"/> for the ambient scope
    /// instead of passing a missing binding-id.
    /// </exception>
    Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default);
}

public sealed record NyxIdUserLlmPreferences(
    LLMSelection Selection,
    LLMSelectionPersistenceStatus Status,
    int MaxToolRounds = 0)
{
    public static NyxIdUserLlmPreferences Empty { get; } = new(
        LLMSelectionPolicy.SystemDefaultSelection(),
        LLMSelectionPersistenceStatus.SystemDefault);

    public LLMControlContext ApplyTo(LLMControlContext current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var applied = Status switch
        {
            LLMSelectionPersistenceStatus.SystemDefault => current,
            LLMSelectionPersistenceStatus.Ready => LLMSelectionPolicy.ApplyTo(current, Selection),
            LLMSelectionPersistenceStatus.LegacyRepairRequired => throw new LLMSelectionRepairRequiredException(),
            _ => throw new InvalidOperationException("User LLM selection persistence status is unspecified."),
        };

        return applied with
        {
            MaxToolRoundsOverride = MaxToolRounds > 0
                ? MaxToolRounds
                : applied.MaxToolRoundsOverride,
        };
    }
}

public enum LLMSelectionPersistenceStatus
{
    Unspecified = 0,
    SystemDefault = 1,
    Ready = 2,
    LegacyRepairRequired = 3,
}
