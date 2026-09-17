using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal sealed record ChannelRegistrationAdoptionRequest(
    string? RegistrationId,
    string NyxChannelBotId,
    string? NyxConversationRouteId,
    string? NyxProviderSlug,
    ChannelBotRuntimeConfig? RuntimeConfig);

/// <summary>
/// Resolves the deployed registration request to a verified Bot and a single owned-resource adoption flow.
/// </summary>
internal sealed class ChannelRegistrationAdoptionFacade(
    IChannelBotRegistrationQueryPort queryPort,
    IChannelRegistrationOwnerResolver ownerResolver,
    VerifiedNyxChannelBotDetail.Reader botReader,
    INyxChannelBotAdoptionService adoptionService,
    ChannelAgentKeyWriteMode writeMode = ChannelAgentKeyWriteMode.Disabled)
{
    public async Task<NyxChannelBotAdoptionResult> AdoptAsync(
        ChannelRegistrationAdoptionRequest request,
        ChannelRegistrationServiceSelection serviceSelection,
        string accessToken,
        string scopeId,
        string webhookBaseUrl,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NyxChannelBotIdentity.IsValid(request.NyxChannelBotId))
            return Failure("missing_nyx_channel_bot_id");
        if (string.IsNullOrWhiteSpace(accessToken))
            return Failure("missing_access_token");
        if (!NyxRelayCallbackUrl.IsSecureBaseUrl(webhookBaseUrl))
            return Failure("insecure_webhook_base_url");
        if (string.IsNullOrWhiteSpace(scopeId))
            return Failure("missing_scope_id");
        if (writeMode != ChannelAgentKeyWriteMode.NyxIdDefault)
            return Failure("channel_agent_key_write_gate_closed");
        if (ChannelBotRuntimeConfigValidation.ValidateStructure(request.RuntimeConfig).Count > 0)
            return Failure("invalid_runtime_config");

        var owner = await ownerResolver.ResolveAsync(accessToken, scopeId, ct);
        if (!owner.Succeeded)
            return Failure(owner.ErrorCode);
        // Platform comes only from the caller-authorized Bot detail. Inventory DTOs cannot
        // mint this handoff, and inaccessible Bots never reveal another registration's ID.
        var detail = await botReader.ReadAsync(accessToken, request.NyxChannelBotId, null, owner.Owner!, ct);
        if (!detail.Succeeded)
            return Failure(detail.ErrorCode);
        var bot = detail.Bot!;
        var existing = (await queryPort.QueryAllSnapshotsAsync(ct))
            .Select(static snapshot => snapshot.Registration)
            .FirstOrDefault(registration => !registration.Tombstoned &&
                string.Equals(registration.NyxChannelBotId, bot.Id, StringComparison.Ordinal));
        if (existing is not null)
            return Failure("channel_bot_already_bound") with { ExistingRegistrationId = existing.Id };

        var registrationId = string.IsNullOrWhiteSpace(request.RegistrationId)
            ? Guid.NewGuid().ToString("N")
            : request.RegistrationId.Trim();
        var existingRegistration = await queryPort.GetSnapshotAsync(registrationId, ct);
        if (existingRegistration is not null && !existingRegistration.Registration.Tombstoned)
            return Failure("registration_id_already_exists");

        // Keep the deployed field in the HTTP contract, but a caller-provided route ID
        // does not prove ownership. This registration creates its own route instead.
        if (!string.IsNullOrWhiteSpace(request.NyxConversationRouteId))
            return Failure("channel_route_not_accessible");

        var registration = new ChannelRelayRegistrationRequest(
            bot.Platform.Value,
            accessToken,
            webhookBaseUrl,
            scopeId,
            string.Empty,
            string.IsNullOrWhiteSpace(request.NyxProviderSlug)
                ? $"api-{bot.Platform.Value}-bot"
                : request.NyxProviderSlug.Trim(),
            bot.Id,
            (request.RuntimeConfig?.DefaultSkill?.Name ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant(),
            request.RuntimeConfig,
            serviceSelection,
            registrationId);
        var result = await adoptionService.AdoptAsync(new(bot, registration), ct);
        return result with
        {
            NyxProviderSlug = registration.NyxProviderSlug,
            Error = result.Succeeded ? null : NyxApiResponseHelper.NormalizePublicFailureReason(result.Error),
            ErrorDetail = null,
        };

        static NyxChannelBotAdoptionResult Failure(string error) =>
            new(false, "error", string.Empty, Error: error);
    }
}
