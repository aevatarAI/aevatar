using System.Collections.Concurrent;
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
    private static readonly TimeSpan ProjectionPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IChannelBotRegistrationRuntimeQueryPort _runtimeQueryPort;
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelBotRegistrationTokenRefreshService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ChannelBotRegistrationTokenRefreshResult>>> _inflight =
        new(StringComparer.Ordinal);

    public ChannelBotRegistrationTokenRefreshService(
        IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        ILogger<ChannelBotRegistrationTokenRefreshService> logger)
    {
        _runtimeQueryPort = runtimeQueryPort ?? throw new ArgumentNullException(nameof(runtimeQueryPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
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

        var lazy = _inflight.GetOrAdd(
            registrationId,
            static (id, self) => new Lazy<Task<ChannelBotRegistrationTokenRefreshResult>>(
                () => self.RefreshCoreAndCleanupAsync(id),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<ChannelBotRegistrationTokenRefreshResult> RefreshCoreAndCleanupAsync(string registrationId)
    {
        try
        {
            return await RefreshCoreAsync(registrationId);
        }
        finally
        {
            _inflight.TryRemove(registrationId, out _);
        }
    }

    private async Task<ChannelBotRegistrationTokenRefreshResult> RefreshCoreAsync(string registrationId)
    {
        using var timeoutCts = new CancellationTokenSource(RefreshTimeout);
        var ct = timeoutCts.Token;

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
            registrationId,
            registration.LegacyDirectBinding,
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
        string registrationId,
        ChannelBotLegacyDirectBinding? existingBinding,
        string accessToken,
        string refreshToken,
        CancellationToken ct)
    {
        var versionBefore = await _queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;

        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId,
                        ct);

        var command = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registrationId,
            LegacyDirectBinding = new ChannelBotLegacyDirectBinding
            {
                NyxUserToken = accessToken,
                NyxRefreshToken = refreshToken,
                VerificationToken = existingBinding?.VerificationToken ?? string.Empty,
                CredentialRef = existingBinding?.CredentialRef ?? string.Empty,
                EncryptKey = existingBinding?.EncryptKey ?? string.Empty,
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

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(ProjectionPollInterval, ct);

            var versionAfter = await _queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var registration = await _runtimeQueryPort.GetAsync(registrationId, ct);
            if (registration is null)
                continue;

            if (string.Equals(registration.GetNyxUserToken(), accessToken, StringComparison.Ordinal) &&
                string.Equals(registration.GetNyxRefreshToken(), refreshToken, StringComparison.Ordinal))
            {
                return new ChannelBotRegistrationTokenRefreshResult(true, registration);
            }
        }

        return new ChannelBotRegistrationTokenRefreshResult(
            false,
            Detail: "refresh_writeback_unconfirmed");
    }
}
