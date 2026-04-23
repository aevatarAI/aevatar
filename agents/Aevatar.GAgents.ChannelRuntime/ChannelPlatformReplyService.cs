using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Sends outbound platform replies using the latest public registration view.
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
        if (!firstAttempt.Succeeded)
        {
            _logger.LogWarning(
                "Channel platform reply failed: platform={Platform}, registration={RegistrationId}, detail={Detail}, kind={Kind}",
                adapter.Platform,
                currentRegistration.Id,
                firstAttempt.Detail,
                firstAttempt.FailureKind);
        }

        return firstAttempt;
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
}
