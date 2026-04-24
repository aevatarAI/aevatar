using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed record ChannelBotRegistrationScopeBackfillSelection(
    string? RegistrationId = null,
    string? NyxAgentApiKeyId = null,
    bool Force = false);

internal sealed record ChannelBotRegistrationScopeBackfillAuthorization(
    string? AccessToken = null,
    INyxRelayApiKeyOwnershipVerifier? OwnershipVerifier = null);

internal sealed record ChannelBotRegistrationScopeBackfillResult(
    int EmptyScopeRegistrationsObserved,
    int CandidateRegistrations,
    int BackfilledRegistrations,
    string Note);

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
                0,
                0,
                0,
                "No empty-scope channel bot registrations were observed.");
        }

        var normalizedScopeId = NormalizeOptional(scopeId);
        if (normalizedScopeId is null)
        {
            return new ChannelBotRegistrationScopeBackfillResult(
                emptyScopeRegistrations.Length,
                0,
                0,
                "Empty-scope registrations were observed, but no canonical scope_id was available for repair.");
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
            return new ChannelBotRegistrationScopeBackfillResult(
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                "Empty-scope registrations were observed; pass registration_id or nyx_agent_api_key_id to repair one safely. force=true only applies after a selector matches multiple registrations.");
        }

        if (candidates.Length == 0)
        {
            return new ChannelBotRegistrationScopeBackfillResult(
                emptyScopeRegistrations.Length,
                0,
                0,
                "No empty-scope registration matched the requested repair selector.");
        }

        if (!selection.Force && candidates.Length != 1)
        {
            return new ChannelBotRegistrationScopeBackfillResult(
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                "Multiple empty-scope registrations matched the repair selector; pass force=true to repair all matched registrations.");
        }

        var accessToken = NormalizeOptional(authorization.AccessToken);
        if (accessToken is null || authorization.OwnershipVerifier is null)
        {
            return new ChannelBotRegistrationScopeBackfillResult(
                emptyScopeRegistrations.Length,
                candidates.Length,
                0,
                "Empty-scope registration repair requires NyxID api-key ownership verification.");
        }

        foreach (var entry in candidates)
        {
            var candidateApiKeyId = NormalizeOptional(entry.NyxAgentApiKeyId);
            if (candidateApiKeyId is null)
            {
                return new ChannelBotRegistrationScopeBackfillResult(
                    emptyScopeRegistrations.Length,
                    candidates.Length,
                    0,
                    $"Empty-scope registration '{entry.Id}' is missing nyx_agent_api_key_id; cannot verify ownership.");
            }

            var ownership = await authorization.OwnershipVerifier.VerifyAsync(
                accessToken,
                normalizedScopeId,
                candidateApiKeyId,
                ct);
            if (!ownership.Succeeded)
            {
                return new ChannelBotRegistrationScopeBackfillResult(
                    emptyScopeRegistrations.Length,
                    candidates.Length,
                    0,
                    $"Empty-scope registration '{entry.Id}' failed NyxID api-key ownership verification: {ownership.Detail}");
            }
        }

        foreach (var entry in candidates)
        {
            await ChannelBotRegistrationStoreCommands.DispatchRegisterAsync(
                actorRuntime,
                dispatchPort,
                new ChannelBotRegisterCommand
                {
                    RequestedId = entry.Id,
                    Platform = entry.Platform,
                    NyxProviderSlug = entry.NyxProviderSlug,
                    ScopeId = normalizedScopeId,
                    WebhookUrl = entry.WebhookUrl,
                    NyxChannelBotId = entry.NyxChannelBotId,
                    NyxAgentApiKeyId = entry.NyxAgentApiKeyId,
                    NyxConversationRouteId = entry.NyxConversationRouteId,
                },
                ct);
        }

        return new ChannelBotRegistrationScopeBackfillResult(
            emptyScopeRegistrations.Length,
            candidates.Length,
            candidates.Length,
            "Empty-scope channel bot registrations were backfilled before projection rebuild; backfill re-registers entries and refreshes created_at.");
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
