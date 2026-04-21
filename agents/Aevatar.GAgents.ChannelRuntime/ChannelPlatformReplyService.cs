using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Sends outbound platform replies and performs Lark-specific Nyx token refresh
/// when the stored access token has expired.
/// </summary>
internal sealed class ChannelPlatformReplyService
{
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ChannelBotRegistrationTokenRefreshService _tokenRefreshService;
    private readonly ILogger<ChannelPlatformReplyService> _logger;

    public ChannelPlatformReplyService(
        IChannelBotRegistrationQueryPort queryPort,
        NyxIdApiClient nyxClient,
        ChannelBotRegistrationTokenRefreshService tokenRefreshService,
        ILogger<ChannelPlatformReplyService> logger)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _tokenRefreshService = tokenRefreshService ?? throw new ArgumentNullException(nameof(tokenRefreshService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PlatformReplyDeliveryResult> DeliverAsync(
        IPlatformAdapter adapter,
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(registration);

        var currentRegistration = await ResolveCurrentRegistrationAsync(registration, ct);
        var firstAttempt = await adapter.SendReplyAsync(
            replyText,
            inbound,
            currentRegistration,
            _nyxClient,
            ct);

        if (IsMissingRefreshTokenFailure(adapter, currentRegistration, firstAttempt))
        {
            return new PlatformReplyDeliveryResult(
                false,
                CombineDetails(firstAttempt.Detail, "manual_reauth_required missing_nyx_refresh_token"),
                PlatformReplyFailureKind.Permanent);
        }

        if (!ShouldAttemptRefresh(adapter, currentRegistration, firstAttempt))
            return firstAttempt;

        var refresh = await _tokenRefreshService.RefreshAsync(currentRegistration.Id, ct);
        if (!refresh.Succeeded || refresh.Registration is null)
        {
            var detail = CombineDetails(
                firstAttempt.Detail,
                refresh.Detail);
            _logger.LogWarning(
                "Lark outbound reply refresh failed: registration={RegistrationId}, detail={Detail}",
                currentRegistration.Id,
                detail);
            return new PlatformReplyDeliveryResult(false, detail, PlatformReplyFailureKind.Permanent);
        }

        var replay = await adapter.SendReplyAsync(
            replyText,
            inbound,
            refresh.Registration,
            _nyxClient,
            ct);

        if (replay.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(replay.Detail)
                ? "auto_refresh_succeeded"
                : $"auto_refresh_succeeded {replay.Detail}";
            _logger.LogInformation(
                "Lark outbound reply recovered after Nyx token refresh: registration={RegistrationId}, detail={Detail}",
                currentRegistration.Id,
                detail);
            return new PlatformReplyDeliveryResult(true, detail);
        }

        var replayDetail = CombineDetails("auto_refresh_succeeded replay_failed", replay.Detail);
        _logger.LogWarning(
            "Lark outbound reply replay failed after Nyx token refresh: registration={RegistrationId}, detail={Detail}",
            currentRegistration.Id,
            replayDetail);
        return new PlatformReplyDeliveryResult(false, replayDetail, replay.FailureKind);
    }

    private async Task<ChannelBotRegistrationEntry> ResolveCurrentRegistrationAsync(
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registration.Id))
            return registration;

        var current = await _queryPort.GetAsync(registration.Id, ct);
        return current ?? registration;
    }

    private static bool ShouldAttemptRefresh(
        IPlatformAdapter adapter,
        ChannelBotRegistrationEntry registration,
        PlatformReplyDeliveryResult result)
    {
        if (!string.Equals(adapter.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(registration.Id) ||
            string.IsNullOrWhiteSpace(registration.NyxRefreshToken))
        {
            return false;
        }

        return LarkPlatformAdapter.IsRefreshableAuthFailure(result);
    }

    private static bool IsMissingRefreshTokenFailure(
        IPlatformAdapter adapter,
        ChannelBotRegistrationEntry registration,
        PlatformReplyDeliveryResult result)
    {
        if (!string.Equals(adapter.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(registration.NyxRefreshToken) &&
               LarkPlatformAdapter.IsRefreshableAuthFailure(result);
    }

    private static string CombineDetails(string? primary, string? secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
            return secondary ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secondary))
            return primary;
        return $"{primary}; {secondary}";
    }
}
