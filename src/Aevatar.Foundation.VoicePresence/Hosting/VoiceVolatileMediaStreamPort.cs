using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceVolatileMediaStreamPort(
    IVoicePresenceTransportAttachmentPort transportAttachmentPort,
    IVoicePresenceSessionLeasePort leasePort,
    IEnumerable<VoicePresenceModuleRegistration> moduleRegistrations,
    IServiceProvider serviceProvider,
    IActorDispatchPort dispatchPort)
    : IVoiceVolatileMediaStreamPort
{
    private const string DetachedReason = "host_transport_detached";

    private readonly IVoicePresenceTransportAttachmentPort _transportAttachmentPort =
        transportAttachmentPort ?? throw new ArgumentNullException(nameof(transportAttachmentPort));
    private readonly IVoicePresenceSessionLeasePort _leasePort =
        leasePort ?? throw new ArgumentNullException(nameof(leasePort));
    private readonly IActorDispatchPort _dispatchPort =
        dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IReadOnlyDictionary<string, VoicePresenceModuleRegistration> _registrationsByName =
        BuildRegistrationMap(moduleRegistrations ?? throw new ArgumentNullException(nameof(moduleRegistrations)));
    private readonly ConcurrentDictionary<string, VoiceVolatileMediaRelay> _activeRelays = new(StringComparer.Ordinal);

    public bool SupportsRemoteAudio => true;

    public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(transport);

        if (!_registrationsByName.TryGetValue(handle.ModuleName, out var registration))
            throw new VoiceVolatileMediaStreamUnavailableException();

        VoicePresenceSessionLeaseHandle? attachedHandle = null;
        try
        {
            attachedHandle = await _transportAttachmentPort.AttachAsync(handle, transport, ct);
            if (string.IsNullOrWhiteSpace(attachedHandle.ActiveTransportLeaseId))
                throw new VoiceVolatileMediaStreamUnavailableException();

            VoiceVolatileMediaRelay? relay = null;
            var providerSession = await registration.ConnectProviderSessionAsync(
                _serviceProvider,
                attachedHandle,
                DispatchProviderEventAsync,
                (sessionKey, audioFrame, audioCt) =>
                {
                    _ = sessionKey;
                    return relay == null
                        ? Task.CompletedTask
                        : relay.SendProviderAudioAsync(audioFrame, audioCt);
                },
                ct);

            relay = new VoiceVolatileMediaRelay(
                attachedHandle,
                transport,
                providerSession,
                DispatchTransportControlAsync);

            if (!_activeRelays.TryAdd(attachedHandle.ActiveTransportLeaseId, relay))
            {
                await relay.DisposeAsync();
                throw new InvalidOperationException("Voice transport already attached.");
            }

            relay.Start();
            return BuildLifetimeCompleted(attachedHandle, "transport_relay_completed");
        }
        catch
        {
            if (attachedHandle != null)
                await CleanupFailedAttachAsync(attachedHandle, transport, ct);

            throw;
        }
    }

    public async Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await StopRelayAsync(handle, ct);
        await _transportAttachmentPort.DetachAsync(handle, expectedTransport, ct);
        await _leasePort.ReleaseAsync(handle, DetachedReason, ct);
    }

    public async Task CompleteTransportLifetimeAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceTransportLifetimeCompleted? completed,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var transportLeaseId = string.IsNullOrWhiteSpace(completed?.TransportLeaseId)
            ? handle.ActiveTransportLeaseId
            : completed.TransportLeaseId;
        if (string.IsNullOrWhiteSpace(transportLeaseId))
            return;

        await StopRelayAsync(transportLeaseId);
        await _leasePort.CompleteTransportLifetimeAsync(handle, transportLeaseId, reason, ct);
    }

    private Task StopRelayAsync(VoicePresenceSessionLeaseHandle handle, CancellationToken ct)
    {
        _ = ct;
        var transportLeaseId = handle.ActiveTransportLeaseId;
        return string.IsNullOrWhiteSpace(transportLeaseId)
            ? Task.CompletedTask
            : StopRelayAsync(transportLeaseId);
    }

    private async Task CleanupFailedAttachAsync(
        VoicePresenceSessionLeaseHandle attachedHandle,
        IVoiceTransport transport,
        CancellationToken ct)
    {
        await _transportAttachmentPort.DetachAsync(attachedHandle, transport, ct);
        await _leasePort.ReleaseAsync(attachedHandle, DetachedReason, ct);
    }

    private async Task StopRelayAsync(string transportLeaseId)
    {
        if (_activeRelays.TryRemove(transportLeaseId, out var relay))
            await relay.DisposeAsync();
    }

    private Task DispatchProviderEventAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderEvent providerEvent,
        CancellationToken ct)
    {
        if (providerEvent.EventCase == VoiceProviderEvent.EventOneofCase.None)
            return Task.CompletedTask;

        return _dispatchPort.DispatchAsync(
            sessionKey.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                sessionKey.ActorId,
                sessionKey.ModuleName,
                new VoiceProviderEventReceived
                {
                    SessionId = sessionKey.SessionId,
                    OwnerId = sessionKey.OwnerId,
                    TransportLeaseId = sessionKey.TransportLeaseId,
                    LeaseExpiresAt = sessionKey.LeaseExpiresAt?.Clone(),
                    ProviderEvent = providerEvent.Clone(),
                }),
            ct);
    }

    private Task DispatchTransportControlAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceControlFrame controlFrame,
        CancellationToken ct) =>
        _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceTransportControlFrameReceived
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = handle.ActiveTransportLeaseId ?? string.Empty,
                    LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                    ControlFrame = controlFrame.Clone(),
                }),
            ct);

    private static VoiceTransportLifetimeCompleted BuildLifetimeCompleted(
        VoicePresenceSessionLeaseHandle handle,
        string reason) =>
        new()
        {
            SessionId = handle.SessionId,
            OwnerId = handle.OwnerId,
            TransportLeaseId = handle.ActiveTransportLeaseId ?? string.Empty,
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
            Reason = reason,
        };

    private static IReadOnlyDictionary<string, VoicePresenceModuleRegistration> BuildRegistrationMap(
        IEnumerable<VoicePresenceModuleRegistration> moduleRegistrations)
    {
        var map = new Dictionary<string, VoicePresenceModuleRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in moduleRegistrations)
        {
            foreach (var name in registration.Names)
            {
                if (!map.TryAdd(name, registration))
                    throw new InvalidOperationException($"Duplicate voice presence module name '{name}' found.");
            }
        }

        return map;
    }

    private sealed class VoiceVolatileMediaRelay(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        RealtimeVoiceProviderSession providerSession,
        Func<VoicePresenceSessionLeaseHandle, VoiceControlFrame, CancellationToken, Task> controlSink)
        : IAsyncDisposable
    {
        private readonly CancellationTokenSource _relayCancellation = new();
        private Task? _relayTask;
        private bool _disposed;

        public void Start()
        {
            _relayTask = RunAsync(_relayCancellation.Token);
        }

        public Task SendProviderAudioAsync(VoiceProviderAudioFrame audioFrame, CancellationToken ct)
        {
            if (audioFrame.Pcm16.IsEmpty)
                return Task.CompletedTask;

            return transport.SendAudioAsync(audioFrame.Pcm16, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _relayCancellation.Cancel();
            await AwaitRelayAsync(_relayTask);
            _relayCancellation.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in transport.ReceiveFramesAsync(ct).WithCancellation(ct))
                {
                    if (frame.IsAudio)
                    {
                        await providerSession.SendAudioAsync(frame.AudioPcm16, ct);
                    }
                    else if (frame.Control != null)
                    {
                        await controlSink(handle, frame.Control, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                await transport.DisposeAsync();
                await providerSession.DisposeAsync();
            }
        }

        private static async Task AwaitRelayAsync(Task? relayTask)
        {
            if (relayTask == null)
                return;

            try
            {
                await relayTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
