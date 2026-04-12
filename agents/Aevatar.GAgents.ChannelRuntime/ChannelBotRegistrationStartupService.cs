using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup. Without this, the scope agent never subscribes
/// to the actor's event stream after a restart, and old registrations
/// are lost from the InMemory store.
///
/// Retries on transient failure (e.g. document store temporarily unavailable)
/// so that registrations become visible deterministically rather than
/// requiring a later register flow to re-activate the scope.
/// </summary>
public sealed class ChannelBotRegistrationStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelBotRegistrationProjectionPort _projectionPort;
    private readonly ILogger<ChannelBotRegistrationStartupService> _logger;
    private CancellationTokenSource? _retryCts;

    public ChannelBotRegistrationStartupService(
        ChannelBotRegistrationProjectionPort projectionPort,
        ILogger<ChannelBotRegistrationStartupService> logger)
    {
        _projectionPort = projectionPort;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ActivateWithRetryAsync(_retryCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        return Task.CompletedTask;
    }

    private async Task ActivateWithRetryAsync(CancellationToken ct)
    {
        var delay = InitialDelay;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _projectionPort.EnsureProjectionForActorAsync(
                    ChannelBotRegistrationGAgent.WellKnownId, ct);
                _logger.LogInformation(
                    "Channel bot registration projection scope activated for {ActorId} (attempt {Attempt})",
                    ChannelBotRegistrationGAgent.WellKnownId, attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to activate channel bot registration projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt, MaxRetries);

                if (attempt < MaxRetries)
                {
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { return; }
                    delay *= 2; // exponential backoff
                }
            }
        }

        _logger.LogError(
            "Channel bot registration projection scope activation failed after {MaxRetries} attempts — " +
            "registrations may not be visible until a new registration triggers re-activation",
            MaxRetries);
    }
}
