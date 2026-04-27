using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed record ChannelBotRegistrationScopeBackfillSelection(
    string? RegistrationId = null,
    string? NyxAgentApiKeyId = null,
    bool Force = false);

internal sealed record ChannelBotRegistrationScopeBackfillAuthorization(
    string? AccessToken = null,
    INyxRelayApiKeyOwnershipVerifier? OwnershipVerifier = null);

/// <summary>
/// Stable machine-readable status for the rebuild backfill outcome. Surfaced
/// to CLI/UI callers so a 202 rebuild dispatch is not misread as a successful
/// backfill — see issue #391.
/// </summary>
internal enum ChannelBotRegistrationScopeBackfillStatus
{
    NotRequired,
    Skipped,
    Rejected,
    // Ownership verified and repair commands dispatched. Application is
    // eventually consistent — repair commands may no-op if the actor's
    // authoritative state diverges from the projection snapshot used to pick
    // candidates, so callers should re-query to confirm completion.
    Dispatched,
    // The query/backfill path threw before a status could be decided. Surfaced
    // so callers always receive a known enum value rather than null.
    Unavailable,
}

internal static class ChannelBotRegistrationScopeBackfillStatusExtensions
{
    // Wire format is snake_case to match the surrounding JSON conventions.
    // Kept explicit so renaming the enum members never silently changes the
    // wire contract that CLI/UI callers branch on.
    public static string ToWireString(this ChannelBotRegistrationScopeBackfillStatus status) => status switch
    {
        ChannelBotRegistrationScopeBackfillStatus.NotRequired => "not_required",
        ChannelBotRegistrationScopeBackfillStatus.Skipped => "skipped",
        ChannelBotRegistrationScopeBackfillStatus.Rejected => "rejected",
        ChannelBotRegistrationScopeBackfillStatus.Dispatched => "dispatched",
        ChannelBotRegistrationScopeBackfillStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown backfill status."),
    };
}

internal sealed record ChannelBotRegistrationScopeBackfillResult(
    ChannelBotRegistrationScopeBackfillStatus Status,
    int EmptyScopeRegistrationsObserved,
    int CandidateRegistrations,
    int RepairCommandsDispatched,
    string Note,
    IReadOnlyList<string> Warnings);

internal static class ChannelBotRegistrationScopeBackfill
{
    public static ChannelBotRegistrationScopeBackfillResult Unavailable(string detail)
    {
        var warning = string.IsNullOrWhiteSpace(detail)
            ? "Channel registration query/backfill path was unavailable; backfill outcome could not be decided."
            : $"Channel registration query/backfill path was unavailable; backfill outcome could not be decided: {detail}";
        return new ChannelBotRegistrationScopeBackfillResult(
            ChannelBotRegistrationScopeBackfillStatus.Unavailable,
            EmptyScopeRegistrationsObserved: 0,
            CandidateRegistrations: 0,
            RepairCommandsDispatched: 0,
            Note: warning,
            Warnings: new[] { warning });
    }

    public static async Task<ChannelBotRegistrationScopeBackfillResult> BackfillAsync(
        IReadOnlyList<ChannelBotRegistrationEntry> registrations,
        string? scopeId,
        ChannelBotRegistrationScopeBackfillSelection selection,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ChannelBotRegistrationScopeBackfillAuthorization authorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(actorRuntime);
        ArgumentNullException.ThrowIfNull(dispatchPort);
        ArgumentNullException.ThrowIfNull(authorization);

        var emptyScopeRegistrations = registrations
            .Where(static entry => string.IsNullOrWhiteSpace(entry.ScopeId))
            .Where(static entry => string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            .Where(static entry => !entry.Tombstoned)
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Id))
            .ToArray();

        if (emptyScopeRegistrations.Length == 0)
        {
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.NotRequired,
                0,
                0,
                0,
                "No empty-scope channel bot registrations were observed.",
                Array.Empty<string>());
        }

        var normalizedScopeId = NormalizeOptional(scopeId);
        if (normalizedScopeId is null)
        {
            const string warning = "Empty-scope registrations were observed, but no canonical scope_id was available for repair.";
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.Skipped,
                emptyScopeRegistrations.Length,
                0,
                0,
                warning,
                new[] { warning });
        }

        var registrationId = NormalizeOptional(selection.RegistrationId);
        var apiKeyId = NormalizeOptional(selection.NyxAgentApiKeyId);
        var candidates = emptyScopeRegistrations
            .Where(entry => registrationId is null || string.Equals(entry.Id, registrationId, StringComparison.Ordinal))
            .Where(entry => apiKeyId is null || string.Equals(entry.NyxAgentApiKeyId, apiKeyId, StringComparison.Ordinal))
            .ToArray();

        var hasExplicitSelector = registrationId is not null || apiKeyId is not null;
        if (!hasExplicitSelector)
        {
            const string warning = "Empty-scope registrations were observed; pass registration_id or nyx_agent_api_key_id to repair one safely. force=true only applies after a selector matches multiple registrations.";
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.Skipped,
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                warning,
                new[] { warning });
        }

        if (candidates.Length == 0)
        {
            const string warning = "No empty-scope registration matched the requested repair selector.";
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.Skipped,
                emptyScopeRegistrations.Length,
                0,
                0,
                warning,
                new[] { warning });
        }

        if (!selection.Force && candidates.Length != 1)
        {
            const string warning = "Multiple empty-scope registrations matched the repair selector; pass force=true to repair all matched registrations.";
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.Skipped,
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                warning,
                new[] { warning });
        }

        var accessToken = NormalizeOptional(authorization.AccessToken);
        if (accessToken is null || authorization.OwnershipVerifier is null)
        {
            const string warning = "Empty-scope registration repair requires NyxID api-key ownership verification.";
            return new ChannelBotRegistrationScopeBackfillResult(
                ChannelBotRegistrationScopeBackfillStatus.Rejected,
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                warning,
                new[] { warning });
        }

        foreach (var entry in candidates)
        {
            var candidateApiKeyId = NormalizeOptional(entry.NyxAgentApiKeyId);
            if (candidateApiKeyId is null)
            {
                var warning = $"Empty-scope registration '{entry.Id}' is missing nyx_agent_api_key_id; cannot verify ownership.";
                return new ChannelBotRegistrationScopeBackfillResult(
                    ChannelBotRegistrationScopeBackfillStatus.Rejected,
                    emptyScopeRegistrations.Length,
                    candidates.Length,
                    0,
                    warning,
                    new[] { warning });
            }

            var ownership = await authorization.OwnershipVerifier.VerifyAsync(
                accessToken,
                normalizedScopeId,
                candidateApiKeyId,
                ct);
            if (!ownership.Succeeded)
            {
                var warning = $"Empty-scope registration '{entry.Id}' failed NyxID api-key ownership verification: {ownership.Detail}";
                return new ChannelBotRegistrationScopeBackfillResult(
                    ChannelBotRegistrationScopeBackfillStatus.Rejected,
                    emptyScopeRegistrations.Length,
                    candidates.Length,
                    0,
                    warning,
                    new[] { warning });
            }
        }

        // Repair-only path: rewrites scope_id while preserving created_at and the
        // rest of the registration shape (issue #391 follow-up 3). The dispatch is
        // fire-and-forget — the authoritative actor may no-op if the candidate has
        // since been tombstoned or already has a matching scope_id, so we surface
        // `dispatched` (not `verified`) to honestly signal eventual consistency.
        foreach (var entry in candidates)
        {
            await ChannelBotRegistrationStoreCommands.DispatchRepairScopeIdAsync(
                actorRuntime,
                dispatchPort,
                entry.Id,
                normalizedScopeId,
                ct);
        }

        return new ChannelBotRegistrationScopeBackfillResult(
            ChannelBotRegistrationScopeBackfillStatus.Dispatched,
            emptyScopeRegistrations.Length,
            candidates.Length,
            candidates.Length,
            "Empty-scope channel bot registration repair commands dispatched (created_at preserved); re-query the registrations endpoint to confirm completion.",
            Array.Empty<string>());
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
