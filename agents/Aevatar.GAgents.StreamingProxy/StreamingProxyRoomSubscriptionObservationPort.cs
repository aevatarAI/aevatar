using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgents.StreamingProxy;

public interface IStreamingProxyRoomSubscriptionObservationPort
{
    Task<StreamingProxyRoomSubscriptionObservationAttachment?> AttachAsync(
        string roomId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default);

    Task DetachAndDisposeAsync(
        StreamingProxyRoomSubscriptionObservationAttachment attachment,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default);
}

// Refactor (iter21/cluster-002-request-path-projection-session-priming):
//   Old pattern: passive room streams ensured projection sessions through endpoint-local priming.
//   New principle: attach-only observation carries explicit leases for deterministic stream cleanup.
public sealed record StreamingProxyRoomSubscriptionObservationAttachment(
    IStreamingProxyRoomSessionProjectionLease ProjectionLease,
    IAsyncDisposable? LiveSinkLease);

internal sealed class StreamingProxyRoomSubscriptionObservationPort
    : IStreamingProxyRoomSubscriptionObservationPort
{
    private readonly IStreamingProxyRoomSessionProjectionPort _projectionPort;

    public StreamingProxyRoomSubscriptionObservationPort(
        IStreamingProxyRoomSessionProjectionPort projectionPort)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public static string RoomSubscriptionSessionId(string roomId) =>
        $"room:{NormalizeRoomId(roomId)}:subscription";

    public async Task<StreamingProxyRoomSubscriptionObservationAttachment?> AttachAsync(
        string roomId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        // Refactor (iter45/issue-867-session-projection-ensure-surface):
        //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
        //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
        ArgumentNullException.ThrowIfNull(sink);

        var normalizedRoomId = NormalizeRoomId(roomId);
        var attachment = await _projectionPort.AttachExistingSubscriptionProjectionAsync(
            normalizedRoomId,
            RoomSubscriptionSessionId(normalizedRoomId),
            sink,
            ct).ConfigureAwait(false);
        return attachment == null
            ? null
            : new StreamingProxyRoomSubscriptionObservationAttachment(
                attachment.ProjectionLease,
                attachment.LiveSinkLease);
    }

    public async Task DetachAndDisposeAsync(
        StreamingProxyRoomSubscriptionObservationAttachment attachment,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(sink);

        Exception? firstException = null;
        try
        {
            await _projectionPort.DetachLiveSinkAsync(attachment.LiveSinkLease, ct);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            sink.Complete();
            await sink.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (firstException != null)
            throw firstException;
    }

    private static string NormalizeRoomId(string roomId)
    {
        var normalized = roomId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Room id is required.", nameof(roomId));

        return normalized;
    }
}
