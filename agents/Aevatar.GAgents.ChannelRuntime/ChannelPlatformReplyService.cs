using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Sends outbound platform replies.
/// Direct Lark replies fail fast with manual re-auth when the persisted
/// Nyx session token has expired.
/// </summary>
internal sealed class ChannelPlatformReplyService
{
    private readonly IChannelBotRegistrationRuntimeQueryPort _runtimeQueryPort;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelPlatformReplyService> _logger;

    public ChannelPlatformReplyService(
        IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        NyxIdApiClient nyxClient,
        ILogger<ChannelPlatformReplyService> logger)
    {
        _runtimeQueryPort = runtimeQueryPort ?? throw new ArgumentNullException(nameof(runtimeQueryPort));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
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

        var manualReauthFailure = BuildManualReauthFailure(adapter, currentRegistration, firstAttempt);
        if (manualReauthFailure is null)
            return firstAttempt;

        _logger.LogWarning(
            "Lark outbound reply requires manual re-auth: registration={RegistrationId}, detail={Detail}",
            currentRegistration.Id,
            manualReauthFailure.Value.Detail);
        return manualReauthFailure.Value;
    }

    private async Task<ChannelBotRegistrationEntry> ResolveCurrentRegistrationAsync(
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registration.Id))
            return registration;

        var current = await _runtimeQueryPort.GetAsync(registration.Id, ct);
        return current ?? registration;
    }

    private static PlatformReplyDeliveryResult? BuildManualReauthFailure(
        IPlatformAdapter adapter,
        ChannelBotRegistrationEntry registration,
        PlatformReplyDeliveryResult result)
    {
        if (!string.Equals(adapter.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!LarkPlatformAdapter.IsRefreshableAuthFailure(result))
            return null;

        var detail = string.IsNullOrWhiteSpace(registration.GetNyxRefreshToken())
            ? CombineDetails(result.Detail, "manual_reauth_required missing_nyx_refresh_token")
            : CombineDetails(result.Detail, "manual_reauth_required reply_path_token_refresh_disabled");
        return new PlatformReplyDeliveryResult(false, detail, PlatformReplyFailureKind.Permanent);
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
