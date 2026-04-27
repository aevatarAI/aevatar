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
internal static class ChannelBotRegistrationScopeBackfillStatus
{
    public const string NotRequired = "not_required";
    public const string Skipped = "skipped";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
}

internal sealed record ChannelBotRegistrationScopeBackfillResult(
    string Status,
    int EmptyScopeRegistrationsObserved,
    int CandidateRegistrations,
    int BackfilledRegistrations,
    string Note,
    IReadOnlyList<string> Warnings);

internal static class ChannelBotRegistrationScopeBackfill
{
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
        // rest of the registration shape (issue #391 follow-up 3).
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
            ChannelBotRegistrationScopeBackfillStatus.Verified,
            emptyScopeRegistrations.Length,
            candidates.Length,
            candidates.Length,
            "Empty-scope channel bot registrations were repaired in place; created_at preserved.",
            Array.Empty<string>());
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
