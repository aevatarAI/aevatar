using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI;

internal sealed class ReadinessGatedRealtimeVoiceProviderSession(
    RealtimeVoiceProviderSession innerSession,
    Task readiness,
    CancellationTokenSource readinessCancellation,
    ILogger<ReadinessGatedRealtimeVoiceProviderSession>? logger = null)
    : RealtimeVoiceProviderSession
{
    private readonly RealtimeVoiceProviderSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly Task _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
    private readonly CancellationTokenSource _readinessCancellation =
        readinessCancellation ?? throw new ArgumentNullException(nameof(readinessCancellation));
    private readonly ILogger<ReadinessGatedRealtimeVoiceProviderSession>? _logger = logger;
    private bool _disposed;

    public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
        _innerSession.SendAudioAsync(pcm16, ct);

    public override async Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct)
    {
        await AwaitReadinessAsync(ct);
        await _innerSession.SendInputImageAsync(inputImage, ct);
    }

    public override async Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
    {
        await AwaitReadinessAsync(ct);
        await _innerSession.SendToolResultAsync(callId, resultJson, ct);
    }

    public override async Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
    {
        await AwaitReadinessAsync(ct);
        await _innerSession.InjectEventAsync(injection, ct);
    }

    public override Task CancelResponseAsync(CancellationToken ct) =>
        _innerSession.CancelResponseAsync(ct);

    public override async Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
    {
        await AwaitReadinessAsync(ct);
        await _innerSession.UpdateSessionAsync(session, ct);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _readinessCancellation.CancelAsync();
        try
        {
            await AwaitReadinessForDisposeAsync();
        }
        finally
        {
            _readinessCancellation.Dispose();
            await _innerSession.DisposeAsync();
        }
    }

    private async Task AwaitReadinessAsync(CancellationToken ct)
    {
        await _readiness.WaitAsync(ct);
    }

    private async Task AwaitReadinessForDisposeAsync()
    {
        try
        {
            await _readiness;
        }
        catch (OperationCanceledException) when (_readinessCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voice realtime readiness failed while disposing the provider session.");
        }
    }
}
