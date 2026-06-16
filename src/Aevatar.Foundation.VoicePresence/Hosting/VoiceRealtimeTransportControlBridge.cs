using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public static class VoiceRealtimeTransportControlBridge
{
    private static readonly TimeSpan ControlSendTimeout = TimeSpan.FromSeconds(5);

    public static Task SendSessionAcceptedAsync(
        IVoiceTransport transport,
        VoiceRealtimeSessionAccepted accepted,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(accepted);

        return SendControlWithTimeoutAsync(transport, new VoiceControlFrame
        {
            SessionAccepted = new VoiceTransportSessionAccepted
            {
                ActorId = accepted.ActorId,
                ModuleName = accepted.ModuleName,
                SessionId = accepted.SessionId,
                PcmSampleRateHz = accepted.PcmSampleRateHz,
                ObservedStateVersion = accepted.ObservedStateVersion,
            },
        }, ct);
    }

    public static Task<IAsyncDisposable> SubscribeAsync(
        IServiceProvider services,
        VoiceRealtimeSessionAccepted accepted,
        IVoiceTransport transport,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(transport);

        var hub = services.GetService<IProjectionSessionEventHub<VoiceRealtimeFrame>>();
        return hub == null
            ? Task.FromResult<IAsyncDisposable>(NoOpAsyncDisposable.Instance)
            : hub.SubscribeAsync(
                accepted.ActorId,
                accepted.SessionId,
                frame => SendRealtimeFrameAsync(transport, accepted, frame),
                ct);
    }

    private static async ValueTask SendRealtimeFrameAsync(
        IVoiceTransport transport,
        VoiceRealtimeSessionAccepted accepted,
        VoiceRealtimeFrame frame)
    {
        if (frame.FrameCase == VoiceRealtimeFrame.FrameOneofCase.None)
            return;

        if (!string.Equals(frame.SessionId, accepted.SessionId, StringComparison.Ordinal))
            return;

        if (!string.IsNullOrWhiteSpace(frame.ModuleName) &&
            !string.Equals(frame.ModuleName, accepted.ModuleName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await SendControlWithTimeoutAsync(transport, new VoiceControlFrame
            {
                RealtimeFrame = frame.Clone(),
            }, CancellationToken.None);
        }
        catch
        {
            // A closed client transport must not fail the projection fanout.
        }
    }

    private static async Task SendControlWithTimeoutAsync(
        IVoiceTransport transport,
        VoiceControlFrame frame,
        CancellationToken ct)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(ControlSendTimeout);
        await transport.SendControlAsync(frame, sendCts.Token);
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoOpAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
