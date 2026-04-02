using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.ExternalLinks;
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
internal sealed class ExternalLinkManager : IExternalLinkPort, IAsyncDisposable
{
    private readonly string _actorId;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IEnumerable<IExternalLinkTransportFactory> _transportFactories;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ManagedLink> _links = new();

    public ExternalLinkManager(
        string actorId,
        IActorDispatchPort dispatchPort,
        IEnumerable<IExternalLinkTransportFactory> transportFactories,
        ILogger logger)
    {
        _actorId = actorId;
        _dispatchPort = dispatchPort;
        _transportFactories = transportFactories;
        _logger = logger;
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
            transport.OnStateChanged = (state, reason, innerCt) => OnStateChangedAsync(link, state, reason, innerCt);

            await ConnectLinkAsync(link, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var link in _links.Values)
            await link.DisposeAsync();

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
            StartReconnectLoop(link);
        }
    }

    // ── Reconnection ──────────────────────────────────────────

    private void StartReconnectLoop(ManagedLink link)
    {
        if (link.IsClosed) return;
        var ct = link.LifetimeCts.Token;
        _ = Task.Run(async () => await ReconnectLoopAsync(link, ct), ct);
    }

    private async Task ReconnectLoopAsync(ManagedLink link, CancellationToken ct)
    {
        var options = link.Descriptor.Options ?? new ExternalLinkOptions();

        while (!ct.IsCancellationRequested && !link.IsClosed)
        {
            link.ReconnectAttempt++;
            if (options.MaxReconnectAttempts > 0 && link.ReconnectAttempt > options.MaxReconnectAttempts)
            {
                await DispatchEventAsync(new ExternalLinkDisconnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    Reason = "max reconnect attempts reached",
                    WillReconnect = false,
                    ReconnectAttempt = link.ReconnectAttempt,
                }, ct);
                return;
            }

            var delay = CalculateBackoff(link.ReconnectAttempt, options);
            await DispatchEventAsync(new ExternalLinkReconnectingEvent
            {
                LinkId = link.Descriptor.LinkId,
                Attempt = link.ReconnectAttempt,
                DelayMs = (int)delay.TotalMilliseconds,
            }, ct);

            try
            {
                await Task.Delay(delay, ct);
                await link.Transport.ConnectAsync(link.Descriptor, ct);
                link.IsConnected = true;
                link.ReconnectAttempt = 0;
                await DispatchEventAsync(new ExternalLinkConnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    ConnectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }, ct);
                return; // connected
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed for link '{LinkId}'",
                    link.ReconnectAttempt, link.Descriptor.LinkId);
            }
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
                    StartReconnectLoop(link);
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
                await DispatchEventAsync(new ExternalLinkDisconnectedEvent
                {
                    LinkId = link.Descriptor.LinkId,
                    Reason = reason ?? "closed",
                    WillReconnect = false,
                }, ct);
                break;
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
}
