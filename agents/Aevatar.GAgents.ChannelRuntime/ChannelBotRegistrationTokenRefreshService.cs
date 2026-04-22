using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal readonly record struct ChannelBotRegistrationTokenRefreshResult(
    bool Succeeded,
    ChannelBotRegistrationEntry? Registration = null,
    string? Detail = null);

/// <summary>
/// Refreshes Nyx session tokens for a channel registration and writes the
/// rotated tokens back to the authoritative registration actor.
/// </summary>
internal sealed class ChannelBotRegistrationTokenRefreshService
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private readonly IChannelBotRegistrationRuntimeQueryPort _runtimeQueryPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelBotRegistrationTokenRefreshService> _logger;

    public ChannelBotRegistrationTokenRefreshService(
        IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        ILogger<ChannelBotRegistrationTokenRefreshService> logger)
    {
        _runtimeQueryPort = runtimeQueryPort ?? throw new ArgumentNullException(nameof(runtimeQueryPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChannelBotRegistrationTokenRefreshResult> RefreshAsync(
        string registrationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return new ChannelBotRegistrationTokenRefreshResult(false, Detail: "registration_id_required");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RefreshTimeout);
        return await RefreshCoreAsync(registrationId, timeoutCts.Token);
    }

    private async Task<ChannelBotRegistrationTokenRefreshResult> RefreshCoreAsync(string registrationId, CancellationToken ct)
    {

        var registration = await _runtimeQueryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return new ChannelBotRegistrationTokenRefreshResult(false, Detail: $"registration_not_found:{registrationId}");

        if (string.IsNullOrWhiteSpace(registration.GetNyxRefreshToken()))
        {
            return new ChannelBotRegistrationTokenRefreshResult(
                false,
                Detail: "manual_reauth_required missing_nyx_refresh_token");
        }

        _logger.LogInformation(
            "Refreshing Nyx session token for channel registration {RegistrationId}",
            registrationId);

        var refresh = await _nyxClient.RefreshSessionAsync(registration.GetNyxRefreshToken(), ct);
        if (!refresh.Succeeded || string.IsNullOrWhiteSpace(refresh.AccessToken))
        {
            var detail = string.IsNullOrWhiteSpace(refresh.Detail)
                ? "manual_reauth_required refresh_failed"
                : $"manual_reauth_required refresh_failed {refresh.Detail}";
            return new ChannelBotRegistrationTokenRefreshResult(false, Detail: detail);
        }

        var rotatedRefreshToken = string.IsNullOrWhiteSpace(refresh.RefreshToken)
            ? registration.GetNyxRefreshToken()
            : refresh.RefreshToken;

        var persisted = await PersistRefreshedTokensAsync(
            registration,
            refresh.AccessToken,
            rotatedRefreshToken,
            ct);

        if (!persisted.Succeeded)
            return persisted;

        _logger.LogInformation(
            "Refreshed Nyx session token for channel registration {RegistrationId}",
            registrationId);
        return persisted;
    }

    private async Task<ChannelBotRegistrationTokenRefreshResult> PersistRefreshedTokensAsync(
        ChannelBotRegistrationEntry registration,
        string accessToken,
        string refreshToken,
        CancellationToken ct)
    {
        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId,
                        ct);

        var command = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registration.Id,
            LegacyDirectBinding = new ChannelBotLegacyDirectBinding
            {
                NyxUserToken = accessToken,
                NyxRefreshToken = refreshToken,
                VerificationToken = registration.LegacyDirectBinding?.VerificationToken ?? string.Empty,
                CredentialRef = registration.LegacyDirectBinding?.CredentialRef ?? string.Empty,
                EncryptKey = registration.LegacyDirectBinding?.EncryptKey ?? string.Empty,
            },
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope, ct);

        var updatedRegistration = registration.Clone();
        updatedRegistration.LegacyDirectBinding = command.LegacyDirectBinding?.Clone();
        return new ChannelBotRegistrationTokenRefreshResult(true, updatedRegistration);
    }
}
