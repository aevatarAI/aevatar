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
    IActorDispatchPort dispatchPort,
    IVoiceVolatileToolCredentialPort? toolCredentialPort = null,
    TimeProvider? timeProvider = null)
    : IVoiceVolatileMediaStreamPort
{
    private const string DetachedReason = "host_transport_detached";

    private readonly IVoicePresenceTransportAttachmentPort _transportAttachmentPort =
        transportAttachmentPort ?? throw new ArgumentNullException(nameof(transportAttachmentPort));
    private readonly IVoicePresenceSessionLeasePort _leasePort =
        leasePort ?? throw new ArgumentNullException(nameof(leasePort));
    private readonly IActorDispatchPort _dispatchPort =
        dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    private readonly IVoiceVolatileToolCredentialPort? _toolCredentialPort = toolCredentialPort;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IReadOnlyDictionary<string, VoicePresenceModuleRegistration> _registrationsByName =
        BuildRegistrationMap(moduleRegistrations ?? throw new ArgumentNullException(nameof(moduleRegistrations)));
    private readonly ConcurrentDictionary<string, VoiceVolatileMediaRelay> _activeRelays = new(StringComparer.Ordinal);

    public bool SupportsRemoteAudio => true;

    public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default) =>
        await AttachAsync(handle, transport, null, ct);

    public async Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        VoiceToolCredentialTransportBinding? toolCredentialBinding,
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

            await BindToolCredentialAsync(attachedHandle, toolCredentialBinding, ct);

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
                DispatchTransportControlAsync,
                DispatchInputImageAsync);

            if (!_activeRelays.TryAdd(attachedHandle.ActiveTransportLeaseId, relay))
            {
                await relay.DisposeAsync();
                throw new VoiceTransportAlreadyAttachedException();
            }

            relay.Start(ct => RunLeaseRenewalAsync(attachedHandle, relay, ct));
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

        try
        {
            await StopRelayAsync(handle, ct);
            await _transportAttachmentPort.DetachAsync(handle, expectedTransport, ct);
            await _leasePort.ReleaseAsync(handle, DetachedReason, ct);
        }
        finally
        {
            await ReleaseToolCredentialAsync(handle, CancellationToken.None);
        }
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

        try
        {
            await StopRelayAsync(transportLeaseId);
            await _leasePort.CompleteTransportLifetimeAsync(handle, transportLeaseId, reason, ct);
        }
        finally
        {
            await ReleaseToolCredentialAsync(transportLeaseId, CancellationToken.None);
        }
    }

    public async Task<bool> TrySendToolResultAsync(
        string transportLeaseId,
        string callId,
        string resultJson,
        CancellationToken ct = default)
    {
        if (!TryGetRelay(transportLeaseId, out var relay))
            return false;

        await relay.SendToolResultAsync(callId, resultJson, ct);
        return true;
    }

    public async Task<bool> TryCancelResponseAsync(
        string transportLeaseId,
        CancellationToken ct = default)
    {
        if (!TryGetRelay(transportLeaseId, out var relay))
            return false;

        await relay.CancelResponseAsync(ct);
        return true;
    }

    public async Task<bool> TrySendInputImageAsync(
        string transportLeaseId,
        VoiceInputImage inputImage,
        CancellationToken ct = default)
    {
        if (!TryGetRelay(transportLeaseId, out var relay))
            return false;

        await relay.SendInputImageAsync(inputImage, ct);
        return true;
    }

    public async Task<bool> TryInjectEventAsync(
        string transportLeaseId,
        VoiceConversationEventInjection injection,
        CancellationToken ct = default)
    {
        if (!TryGetRelay(transportLeaseId, out var relay))
            return false;

        await relay.InjectEventAsync(injection, ct);
        return true;
    }

    private bool TryGetRelay(string transportLeaseId, out VoiceVolatileMediaRelay relay)
    {
        relay = null!;
        return !string.IsNullOrWhiteSpace(transportLeaseId) &&
               _activeRelays.TryGetValue(transportLeaseId, out relay!);
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
        try
        {
            await _transportAttachmentPort.DetachAsync(attachedHandle, transport, ct);
            await _leasePort.ReleaseAsync(attachedHandle, DetachedReason, ct);
        }
        finally
        {
            await ReleaseToolCredentialAsync(attachedHandle, CancellationToken.None);
        }
    }

    private async Task StopRelayAsync(string transportLeaseId)
    {
        if (_activeRelays.TryRemove(transportLeaseId, out var relay))
            await relay.DisposeAsync();
    }

    private async Task RunLeaseRenewalAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceVolatileMediaRelay relay,
        CancellationToken ct)
    {
        var renewalInterval = ActorOwnedVoiceRealtimeSession.DefaultLeaseTtl / 2;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(renewalInterval, _timeProvider, ct);
            if (!IsActiveRelay(handle.ActiveTransportLeaseId, relay))
                return;

            await DispatchLeaseRenewalAsync(handle, ct);
        }
    }

    private bool IsActiveRelay(string? transportLeaseId, VoiceVolatileMediaRelay relay) =>
        !string.IsNullOrWhiteSpace(transportLeaseId) &&
        _activeRelays.TryGetValue(transportLeaseId, out var activeRelay) &&
        ReferenceEquals(activeRelay, relay);

    private Task DispatchLeaseRenewalAsync(VoicePresenceSessionLeaseHandle handle, CancellationToken ct) =>
        _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceTransportLeaseRenewRequested
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = handle.ActiveTransportLeaseId ?? string.Empty,
                    LeaseEpoch = handle.LeaseEpoch,
                    RenewExpiresAt = Timestamp.FromDateTimeOffset(
                        _timeProvider.GetUtcNow().Add(ActorOwnedVoiceRealtimeSession.DefaultLeaseTtl)),
                }),
            ct);

    private async Task BindToolCredentialAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceToolCredentialTransportBinding? credentialBinding,
        CancellationToken ct)
    {
        if (_toolCredentialPort is null)
            return;

        var credentialRef = Normalize(handle.ToolContext?.CredentialRef);
        var transportLeaseId = Normalize(handle.ActiveTransportLeaseId);
        if (credentialRef is null || transportLeaseId is null)
            return;

        if (credentialBinding is null ||
            !string.Equals(credentialRef, Normalize(credentialBinding.CredentialRef), StringComparison.Ordinal))
        {
            throw new VoiceVolatileToolCredentialUnavailableException();
        }

        var bound = await _toolCredentialPort.BindTransportLeaseAsync(credentialBinding, transportLeaseId, ct);
        if (!bound)
            throw new VoiceVolatileToolCredentialUnavailableException();
    }

    private Task ReleaseToolCredentialAsync(
        VoicePresenceSessionLeaseHandle handle,
        CancellationToken ct)
    {
        var transportLeaseId = Normalize(handle.ActiveTransportLeaseId);
        return transportLeaseId is null
            ? Task.CompletedTask
            : ReleaseToolCredentialAsync(transportLeaseId, ct);
    }

    private Task ReleaseToolCredentialAsync(string transportLeaseId, CancellationToken ct) =>
        _toolCredentialPort?.ReleaseTransportLeaseAsync(transportLeaseId, ct) ?? Task.CompletedTask;

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
                    LeaseEpoch = sessionKey.LeaseEpoch,
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
                    LeaseEpoch = handle.LeaseEpoch,
                    ControlFrame = controlFrame.Clone(),
                }),
            ct);

    private Task DispatchInputImageAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceInputImage inputImage,
        CancellationToken ct) =>
        _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceInputImageReceived
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = handle.ActiveTransportLeaseId ?? string.Empty,
                    LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                    LeaseEpoch = handle.LeaseEpoch,
                    InputImage = inputImage.Clone(),
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
            LeaseEpoch = handle.LeaseEpoch,
            Reason = reason,
        };

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

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
        Func<VoicePresenceSessionLeaseHandle, VoiceControlFrame, CancellationToken, Task> controlSink,
        Func<VoicePresenceSessionLeaseHandle, VoiceInputImage, CancellationToken, Task> inputImageSink)
        : IAsyncDisposable
    {
        private readonly CancellationTokenSource _relayCancellation = new();
        private Task? _relayTask;
        private bool _disposed;

        private Task? _renewalTask;

        public void Start(Func<CancellationToken, Task> renewalLoop)
        {
            var ct = _relayCancellation.Token;
            _relayTask = RunAsync(ct);
            _renewalTask = renewalLoop(ct);
        }

        public Task SendProviderAudioAsync(VoiceProviderAudioFrame audioFrame, CancellationToken ct)
        {
            if (audioFrame.Pcm16.IsEmpty)
                return Task.CompletedTask;

            return transport.SendAudioAsync(audioFrame.Pcm16, ct);
        }

        public Task CancelResponseAsync(CancellationToken ct) =>
            providerSession.CancelResponseAsync(ct);

        public Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct) =>
            providerSession.SendInputImageAsync(inputImage, ct);

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) =>
            providerSession.SendToolResultAsync(callId, resultJson, ct);

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) =>
            providerSession.InjectEventAsync(injection, ct);

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _relayCancellation.Cancel();
            await AwaitRelayAsync(_relayTask);
            await AwaitRelayAsync(_renewalTask);
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
                    else if (frame.InputImage != null)
                    {
                        await inputImageSink(handle, frame.InputImage, ct);
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
