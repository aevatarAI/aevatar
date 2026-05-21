using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Core.ExternalLinks;

/// <summary>
/// Per-actor connection manager. Holds physical connections in the infrastructure layer
/// and bridges them to the actor event pipeline via <see cref="IActorDispatchPort"/>.
///
/// Lifecycle: created on actor activate, disposed on actor deactivate.
///
/// Known limitations (TODO):
/// - No backpressure on inbound messages.
/// - No connection pooling (each actor holds its own connections).
/// - No authentication credential refresh.
/// - Outbound SendAsync failures are surfaced as exceptions to the caller.
/// </summary>
// Refactor (iter22/cluster-004):
//   Old pattern: External link reconnect loop ran on Task.Run, slept with Task.Delay, and mutated ManagedLink outside the actor turn.
//   New principle: callbacks dispatch typed signals with link id and attempt; actor-turn code validates current state before mutating.
internal sealed class ExternalLinkManager : IExternalLinkPort, IAsyncDisposable
{
    private const string ReconnectCallbackPrefix = "external-link-reconnect";

    private readonly string _actorId;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
    private readonly IEnumerable<IExternalLinkTransportFactory> _transportFactories;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ManagedLink> _links = new();

    public ExternalLinkManager(
        string actorId,
        IActorDispatchPort dispatchPort,
        IActorRuntimeCallbackScheduler callbackScheduler,
        IEnumerable<IExternalLinkTransportFactory> transportFactories,
        ILogger logger)
    {
        _actorId = actorId;
        _dispatchPort = dispatchPort;
        _callbackScheduler = callbackScheduler;
        _transportFactories = transportFactories;
        _logger = logger;
    }

    public bool CanHandle(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return false;

        return envelope.Payload.Is(ExternalLinkReconnectDueSignal.Descriptor)
               || envelope.Payload.Is(ExternalLinkTransportStateChangedSignal.Descriptor);
    }

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(ExternalLinkReconnectDueSignal.Descriptor))
        {
            await HandleReconnectDueAsync(envelope.Payload.Unpack<ExternalLinkReconnectDueSignal>(), ct);
            return;
        }

        if (envelope.Payload.Is(ExternalLinkTransportStateChangedSignal.Descriptor))
            await HandleTransportStateChangedAsync(envelope.Payload.Unpack<ExternalLinkTransportStateChangedSignal>(), ct);
    }

    // ── Lifecycle ─────────────────────────────────────────────

    public async Task StartAsync(IReadOnlyList<ExternalLinkDescriptor> descriptors, CancellationToken ct = default)
    {
        foreach (var descriptor in descriptors)
        {
            var transport = CreateTransport(descriptor.TransportType);
            if (transport == null)
            {
                _logger.LogError(
                    "No transport factory for type '{TransportType}', skipping link '{LinkId}'",
                    descriptor.TransportType, descriptor.LinkId);
                continue;
            }

            var link = new ManagedLink(descriptor, transport);
            _links[descriptor.LinkId] = link;

            transport.OnMessageReceived = (data, innerCt) => OnMessageReceivedAsync(link, data, innerCt);
            transport.OnStateChanged = (state, reason, innerCt) =>
                OnTransportStateChangedSignalAsync(link.Descriptor.LinkId, state, reason, innerCt);

            await ConnectLinkAsync(link, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links.Values)
        {
            await CancelReconnectAsync(link, CancellationToken.None);
            await link.DisposeAsync();
        }

        _links.Clear();
    }

    // ── IExternalLinkPort ─────────────────────────────────────

    public Task SendAsync(string linkId, IMessage payload, CancellationToken ct = default)
    {
        var link = GetLink(linkId);
        var bytes = payload.ToByteArray();
        return link.Transport.SendAsync(bytes, ct);
    }

    public Task SendRawAsync(string linkId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var link = GetLink(linkId);
        return link.Transport.SendAsync(data, ct);
    }

    public async Task DisconnectAsync(string linkId, CancellationToken ct = default)
    {
        var link = GetLink(linkId);
        link.IsClosed = true;
        await CancelReconnectAsync(link, ct);
        link.LifetimeCts.Cancel();
        await link.Transport.DisconnectAsync(ct);
    }

    // ── Connection ────────────────────────────────────────────

    private async Task ConnectLinkAsync(ManagedLink link, CancellationToken ct)
    {
        try
        {
            await link.Transport.ConnectAsync(link.Descriptor, ct);
            link.IsConnected = true;
            link.ReconnectAttempt = 0;
            await CancelReconnectAsync(link, ct);
            await DispatchEventAsync(new ExternalLinkConnectedEvent
            {
                LinkId = link.Descriptor.LinkId,
                ConnectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to connect link '{LinkId}'", link.Descriptor.LinkId);
            link.IsConnected = false;
            await ScheduleReconnectAsync(link, nextAttempt: 1, ct);
        }
    }

    // ── Reconnection ──────────────────────────────────────────

    private async Task ScheduleReconnectAsync(ManagedLink link, int nextAttempt, CancellationToken ct)
    {
        if (link.IsClosed)
            return;

        link.ReconnectAttempt = nextAttempt;
        var options = link.Descriptor.Options ?? new ExternalLinkOptions();
        if (options.MaxReconnectAttempts > 0 && nextAttempt > options.MaxReconnectAttempts)
        {
            await DispatchEventAsync(new ExternalLinkDisconnectedEvent
            {
                LinkId = link.Descriptor.LinkId,
                Reason = "max reconnect attempts reached",
                WillReconnect = false,
                ReconnectAttempt = nextAttempt,
            }, ct);
            return;
        }

        var delay = CalculateBackoff(nextAttempt, options);
        await DispatchEventAsync(new ExternalLinkReconnectingEvent
        {
            LinkId = link.Descriptor.LinkId,
            Attempt = nextAttempt,
            DelayMs = (int)delay.TotalMilliseconds,
        }, ct);

        var signal = new ExternalLinkReconnectDueSignal
        {
            LinkId = link.Descriptor.LinkId,
            ExpectedAttempt = nextAttempt,
        };
        link.ReconnectLease = await ScheduleSignalAfterDelayAsync(
            BuildReconnectCallbackId(link.Descriptor.LinkId),
            delay,
            signal,
            ct);
    }

    private async Task HandleReconnectDueAsync(
        ExternalLinkReconnectDueSignal signal,
        CancellationToken ct)
    {
        if (!_links.TryGetValue(signal.LinkId, out var link))
            return;

        if (link.IsClosed || link.IsConnected || signal.ExpectedAttempt != link.ReconnectAttempt)
            return;

        try
        {
            await link.Transport.ConnectAsync(link.Descriptor, ct);
            link.IsConnected = true;
            link.ReconnectAttempt = 0;
            await CancelReconnectAsync(link, ct);
            await DispatchEventAsync(new ExternalLinkConnectedEvent
            {
                LinkId = link.Descriptor.LinkId,
                ConnectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed for link '{LinkId}'",
                signal.ExpectedAttempt, link.Descriptor.LinkId);
            await ScheduleReconnectAsync(link, signal.ExpectedAttempt + 1, ct);
        }
    }

    private static TimeSpan CalculateBackoff(int attempt, ExternalLinkOptions options)
    {
        var baseMs = options.ReconnectBaseDelay.TotalMilliseconds;
        var maxMs = options.ReconnectMaxDelay.TotalMilliseconds;
        var delayMs = Math.Min(baseMs * Math.Pow(2, attempt - 1), maxMs);
        // add jitter ±20%
        var jitter = (Random.Shared.NextDouble() * 0.4 - 0.2) * delayMs;
        return TimeSpan.FromMilliseconds(Math.Max(delayMs + jitter, 100));
    }

    // ── Transport callbacks ───────────────────────────────────

    private async Task OnMessageReceivedAsync(ManagedLink link, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var evt = new ExternalLinkMessageReceivedEvent
        {
            LinkId = link.Descriptor.LinkId,
            RawPayload = Google.Protobuf.ByteString.CopyFrom(data.Span),
            ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await DispatchEventAsync(evt, ct);
    }

    private async Task OnStateChangedAsync(
        ManagedLink link, ExternalLinkStateChange state, string? reason, CancellationToken ct)
    {
        switch (state)
        {
            case ExternalLinkStateChange.Connected:
                link.IsConnected = true;
                link.ReconnectAttempt = 0;
                await CancelReconnectAsync(link, ct);
                await DispatchEventAsync(new ExternalLinkConnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    ConnectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }, ct);
                break;

            case ExternalLinkStateChange.Disconnected:
                link.IsConnected = false;
                var willReconnect = !link.IsClosed;
                await DispatchEventAsync(new ExternalLinkDisconnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    Reason = reason ?? "transport disconnected",
                    WillReconnect = willReconnect,
                    ReconnectAttempt = link.ReconnectAttempt,
                }, ct);
                if (willReconnect)
                    await ScheduleReconnectAsync(link, link.ReconnectAttempt + 1, ct);
                break;

            case ExternalLinkStateChange.Error:
                await DispatchEventAsync(new ExternalLinkErrorEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    ErrorMessage = reason ?? "unknown error",
                }, ct);
                break;

            case ExternalLinkStateChange.Closed:
                link.IsClosed = true;
                link.IsConnected = false;
                await CancelReconnectAsync(link, ct);
                await DispatchEventAsync(new ExternalLinkDisconnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    Reason = reason ?? "closed",
                    WillReconnect = false,
                }, ct);
                break;
        }
    }

    private async Task HandleTransportStateChangedAsync(
        ExternalLinkTransportStateChangedSignal signal,
        CancellationToken ct)
    {
        if (!_links.TryGetValue(signal.LinkId, out var link))
            return;

        await OnStateChangedAsync(link, ToTransportStateChange(signal.State), EmptyToNull(signal.Reason), ct);
    }

    // Refactor (iter22/cluster-004):
    //   Old pattern: transport callbacks directly mutated ManagedLink or started reconnect loops from I/O callback threads.
    //   New principle: callbacks only signal the actor inbox; state changes happen when the signal is handled in the actor turn.
    private Task OnTransportStateChangedSignalAsync(
        string linkId,
        ExternalLinkStateChange state,
        string? reason,
        CancellationToken ct)
    {
        var signal = new ExternalLinkTransportStateChangedSignal
        {
            LinkId = linkId,
            State = ToSignalKind(state),
            Reason = reason ?? string.Empty,
        };
        return DispatchSignalAsync(signal, ct);
    }

    private Task<RuntimeCallbackLease> ScheduleSignalAfterDelayAsync(
        string callbackId,
        TimeSpan delay,
        IMessage signal,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(_actorId, TopologyAudience.Self),
        };

        return _callbackScheduler.ScheduleTimeoutAsync(
            new RuntimeCallbackTimeoutRequest
            {
                ActorId = _actorId,
                CallbackId = callbackId,
                TriggerEnvelope = envelope,
                DueTime = delay,
            },
            ct);
    }

    private async Task CancelReconnectAsync(ManagedLink link, CancellationToken ct)
    {
        if (link.ReconnectLease == null)
            return;

        try
        {
            await _callbackScheduler.CancelAsync(link.ReconnectLease, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to cancel reconnect callback for link '{LinkId}'", link.Descriptor.LinkId);
        }
        finally
        {
            link.ReconnectLease = null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────

    private ManagedLink GetLink(string linkId)
    {
        if (!_links.TryGetValue(linkId, out var link))
            throw new InvalidOperationException($"External link '{linkId}' not found on actor '{_actorId}'.");
        return link;
    }

    private IExternalLinkTransport? CreateTransport(string transportType)
    {
        foreach (var factory in _transportFactories)
        {
            if (factory.CanCreate(transportType))
                return factory.Create();
        }
        return null;
    }

    private Task DispatchSignalAsync(IMessage signal, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(_actorId, TopologyAudience.Self),
        };

        return _dispatchPort.DispatchAsync(_actorId, envelope, ct);
    }

    private Task DispatchEventAsync(IMessage evt, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(_actorId, TopologyAudience.Self),
        };

        return _dispatchPort.DispatchAsync(_actorId, envelope, ct);
    }

    private static string BuildReconnectCallbackId(string linkId) => $"{ReconnectCallbackPrefix}:{linkId}";

    private static string? EmptyToNull(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static ExternalLinkTransportStateSignalKind ToSignalKind(ExternalLinkStateChange state) =>
        state switch
        {
            ExternalLinkStateChange.Connected => ExternalLinkTransportStateSignalKind.Connected,
            ExternalLinkStateChange.Disconnected => ExternalLinkTransportStateSignalKind.Disconnected,
            ExternalLinkStateChange.Error => ExternalLinkTransportStateSignalKind.Error,
            ExternalLinkStateChange.Closed => ExternalLinkTransportStateSignalKind.Closed,
            _ => ExternalLinkTransportStateSignalKind.Unspecified,
        };

    private static ExternalLinkStateChange ToTransportStateChange(ExternalLinkTransportStateSignalKind state) =>
        state switch
        {
            ExternalLinkTransportStateSignalKind.Connected => ExternalLinkStateChange.Connected,
            ExternalLinkTransportStateSignalKind.Disconnected => ExternalLinkStateChange.Disconnected,
            ExternalLinkTransportStateSignalKind.Error => ExternalLinkStateChange.Error,
            ExternalLinkTransportStateSignalKind.Closed => ExternalLinkStateChange.Closed,
            _ => ExternalLinkStateChange.Error,
        };
}
