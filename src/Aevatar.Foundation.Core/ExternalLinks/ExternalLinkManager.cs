using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.Pipeline;
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

    // Refactor (iter22/cluster-004):
    //   Old pattern: callback envelopes were indistinguishable from user-observable events in the normal handler pipeline.
    //   New principle: the manager advertises only its typed internal callback signals for actor-turn short-circuiting.
    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // Inbound transport messages now enter through ExternalLinkMessageReceivedSignal.
    // The regular event pipeline still only sees committed external-link events.
    public bool CanHandle(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return false;

        return envelope.Payload.Is(ExternalLinkReconnectDueSignal.Descriptor)
               || envelope.Payload.Is(ExternalLinkMessageReceivedSignal.Descriptor)
               || envelope.Payload.Is(ExternalLinkTransportStateChangedSignal.Descriptor);
    }

    // Refactor (iter22/cluster-004):
    //   Old pattern: callback work could continue on background threads after transport callbacks or delayed reconnect loops.
    //   New principle: internal callback envelopes are unpacked and handled as explicit actor-turn signals.
    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // Message/state signals are consumed here before business events are emitted.
    // Link existence is reconciled inside the manager's actor-turn handling.
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(ExternalLinkReconnectDueSignal.Descriptor))
        {
            await HandleReconnectDueAsync(envelope.Payload.Unpack<ExternalLinkReconnectDueSignal>(), ct);
            return;
        }

        if (envelope.Payload.Is(ExternalLinkMessageReceivedSignal.Descriptor))
        {
            await HandleMessageReceivedAsync(envelope.Payload.Unpack<ExternalLinkMessageReceivedSignal>(), ct);
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

            // Refactor (iter56/cluster-912-external-link-signal-contract):
            // old=transport direct callback, new=typed signal sink.
            // The transport receives only a sink that can publish internal signals.
            // This manager stamps link identity and dispatches to the actor inbox.
            transport.SignalSink = new ExternalLinkTransportSignalSink(
                descriptor.LinkId,
                DispatchSignalAsync);

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
        await link.Transport.DisconnectAsync(ct);
    }

    // ── Connection ────────────────────────────────────────────

    // Refactor (iter22/cluster-004):
    //   Old pattern: a failed connect started a background reconnect loop from inside the connection helper.
    //   New principle: failed connects schedule one typed reconnect callback that must re-enter the actor turn.
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

    // Refactor (iter22/cluster-004):
    //   Old pattern: reconnect scheduling created a background loop that slept and mutated link state outside actor handling.
    //   New principle: scheduling records the expected attempt and asks the runtime callback scheduler to send a typed self signal.
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

    // Refactor (iter22/cluster-004):
    //   Old pattern: reconnect retry logic ran in a long-lived background task with stale in-memory access to ManagedLink.
    //   New principle: each retry is a checked actor-turn signal; stale attempts are ignored before any transport mutation.
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

    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // Public ExternalLinkMessageReceivedEvent is emitted from a handled signal.
    // The transport no longer invokes this conversion directly.
    private async Task OnMessageReceivedAsync(ManagedLink link, ExternalLinkMessageReceivedSignal signal, CancellationToken ct)
    {
        var evt = new ExternalLinkMessageReceivedEvent
        {
            LinkId = link.Descriptor.LinkId,
            RawPayload = signal.RawPayload,
            ReceivedAt = signal.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await DispatchEventAsync(evt, ct);
    }

    // Refactor (iter22/cluster-004):
    //   Old pattern: transport state callbacks directly changed link state and started reconnect loops.
    //   New principle: this state transition method is only reached after a typed state signal is consumed in the actor turn.
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
    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // Transport-owned callbacks publish protobuf signals with link identity.
    // Business events are emitted only after this manager handles the signal.
    private async Task HandleMessageReceivedAsync(
        ExternalLinkMessageReceivedSignal signal,
        CancellationToken ct)
    {
        if (!_links.TryGetValue(signal.LinkId, out var link))
            return;

        await OnMessageReceivedAsync(link, signal, ct);
    }

    private Task<RuntimeCallbackLease> ScheduleSignalAfterDelayAsync(
        string callbackId,
        TimeSpan delay,
        IMessage signal,
        CancellationToken ct)
    {
        return _callbackScheduler.ScheduleTimeoutAsync(
            new RuntimeCallbackTimeoutRequest
            {
                ActorId = _actorId,
                CallbackId = callbackId,
                TriggerEnvelope = SelfEventEnvelopeFactory.Create(_actorId, signal),
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
        var envelope = SelfEventEnvelopeFactory.Create(_actorId, signal);
        return _dispatchPort.DispatchAsync(_actorId, envelope, ct);
    }

    private Task DispatchEventAsync(IMessage evt, CancellationToken ct)
    {
        var envelope = SelfEventEnvelopeFactory.Create(_actorId, evt);
        return _dispatchPort.DispatchAsync(_actorId, envelope, ct);
    }

    private static string BuildReconnectCallbackId(string linkId) => $"{ReconnectCallbackPrefix}:{linkId}";

    private static string? EmptyToNull(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static ExternalLinkStateChange ToTransportStateChange(ExternalLinkTransportStateSignalKind state) =>
        state switch
        {
            ExternalLinkTransportStateSignalKind.Connected => ExternalLinkStateChange.Connected,
            ExternalLinkTransportStateSignalKind.Disconnected => ExternalLinkStateChange.Disconnected,
            ExternalLinkTransportStateSignalKind.Error => ExternalLinkStateChange.Error,
            ExternalLinkTransportStateSignalKind.Closed => ExternalLinkStateChange.Closed,
            _ => ExternalLinkStateChange.Error,
        };

    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // This adapter stamps link identity on transport signals and dispatches them.
    // Actor/module turns remain the only place where link facts are changed.
    private sealed class ExternalLinkTransportSignalSink(
        string linkId,
        Func<IMessage, CancellationToken, Task> dispatchSignalAsync) : IExternalLinkSignalSink
    {
        public Task PublishMessageReceivedAsync(ExternalLinkMessageReceivedSignal signal, CancellationToken ct)
        {
            signal.LinkId = linkId;
            if (signal.ReceivedAt == null)
                signal.ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow);
            return dispatchSignalAsync(signal, ct);
        }

        public Task PublishStateChangedAsync(ExternalLinkTransportStateChangedSignal signal, CancellationToken ct)
        {
            signal.LinkId = linkId;
            return dispatchSignalAsync(signal, ct);
        }
    }
}
