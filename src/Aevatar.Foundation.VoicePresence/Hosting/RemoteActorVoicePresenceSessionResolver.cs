using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Resolves runtime-neutral host sessions that bridge transports through actor dispatch + actor stream observation.
/// </summary>
public sealed class RemoteActorVoicePresenceSessionResolver : IVoicePresenceSessionResolver
{
    private const string DefaultVoiceModuleName = "voice_presence";
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, VoicePresenceModuleRegistration> _registrationsByName;
    private readonly IReadOnlyList<VoicePresenceModuleRegistration> _registrations;

    public RemoteActorVoicePresenceSessionResolver(
        IServiceProvider services,
        IEnumerable<VoicePresenceModuleRegistration>? registrations = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrations = registrations?.ToArray() ?? [];
        _registrationsByName = _registrations
            .SelectMany(static registration => registration.Names.Select(name => (Name: name, Registration: registration)))
            .ToDictionary(static pair => pair.Name, static pair => pair.Registration, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ct.ThrowIfCancellationRequested();

        var actorRuntime = _services.GetService<IActorRuntime>();
        var dispatchPort = _services.GetService<IActorDispatchPort>();
        var subscriptions = _services.GetService<IActorEventSubscriptionProvider>();
        if (actorRuntime == null || dispatchPort == null || subscriptions == null)
            return null;

        if (!await actorRuntime.ExistsAsync(request.ActorId))
            return null;

        var target = ResolveTargetModule(request.ModuleName);
        if (target == null)
            return null;

        var bridge = new RemoteActorVoicePresenceSessionBridge(
            request.ActorId,
            target.Value.ModuleName,
            target.Value.PcmSampleRateHz,
            dispatchPort,
            subscriptions);

        return bridge.CreateSession();
    }

    private (string ModuleName, int PcmSampleRateHz)? ResolveTargetModule(string? requestedModuleName)
    {
        if (!string.IsNullOrWhiteSpace(requestedModuleName))
        {
            var normalized = requestedModuleName.Trim();
            if (_registrationsByName.TryGetValue(normalized, out var registration))
                return (normalized, registration.PcmSampleRateHz);

            return _registrationsByName.Count == 0
                ? (normalized, Transport.WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz)
                : null;
        }

        if (_registrationsByName.Count == 0)
        {
            return (
                DefaultVoiceModuleName,
                Transport.WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz);
        }

        if (_registrations.Count == 1)
        {
            var registration = _registrations[0];
            return (registration.Names[0], registration.PcmSampleRateHz);
        }

        return _registrationsByName.TryGetValue(DefaultVoiceModuleName, out var defaultRegistration)
            ? (DefaultVoiceModuleName, defaultRegistration.PcmSampleRateHz)
            : null;
    }

    private sealed class RemoteActorVoicePresenceSessionBridge
    {
        private readonly string _actorId;
        private readonly string _moduleName;
        private readonly int _pcmSampleRateHz;
        private readonly IActorDispatchPort _dispatchPort;
        private readonly IActorEventSubscriptionProvider _subscriptions;
        private readonly Lock _gate = new();
        private AttachmentState? _state;

        public RemoteActorVoicePresenceSessionBridge(
            string actorId,
            string moduleName,
            int pcmSampleRateHz,
            IActorDispatchPort dispatchPort,
            IActorEventSubscriptionProvider subscriptions)
        {
            _actorId = actorId;
            _moduleName = moduleName;
            _pcmSampleRateHz = pcmSampleRateHz;
            _dispatchPort = dispatchPort;
            _subscriptions = subscriptions;
        }

        public VoicePresenceSession CreateSession() =>
            new(
                isInitialized: static () => true,
                isTransportAttached: () => TryGetState() != null,
                attachTransportAsync: AttachTransportAsync,
                detachTransportAsync: DetachTransportAsync,
                pcmSampleRateHz: _pcmSampleRateHz);

        private async Task AttachTransportAsync(IVoiceTransport transport, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ct.ThrowIfCancellationRequested();

            var sessionId = Guid.NewGuid().ToString("N");
            var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var subscription = await _subscriptions.SubscribeAsync<VoiceRemoteTransportOutput>(
                _actorId,
                HandleOutputAsync,
                ct);
            var state = new AttachmentState(sessionId, transport, relayCts, subscription);

            lock (_gate)
            {
                if (_state != null)
                    throw new InvalidOperationException("A voice transport is already attached.");

                _state = state;
            }

            try
            {
                state.RelayTask = RunTransportRelayAsync(state);
                await DispatchAsync(new VoiceRemoteSessionOpenRequested
                {
                    SessionId = sessionId,
                }, ct);
            }
            catch
            {
                await ReleaseStateAsync(state, dispatchCloseRequest: false, awaitRelayCompletion: false);
                throw;
            }
        }

        private async Task DetachTransportAsync(IVoiceTransport? expectedTransport, CancellationToken ct)
        {
            var state = TryGetState();
            if (state == null)
            {
                await DispatchCloseRequestAsync(sessionId: null, "host_detach");
                return;
            }

            if (expectedTransport != null && !ReferenceEquals(expectedTransport, state.Transport))
                return;

            _ = ct;
            await ReleaseStateAsync(state, dispatchCloseRequest: true, awaitRelayCompletion: true);
        }

        private async Task RunTransportRelayAsync(AttachmentState state)
        {
            try
            {
                await foreach (var frame in state.Transport.ReceiveFramesAsync(state.RelayCancellation.Token))
                {
                    if (frame.IsAudio)
                    {
                        if (frame.AudioPcm16.IsEmpty)
                            continue;

                        await DispatchAsync(new VoiceRemoteAudioInputReceived
                        {
                            SessionId = state.SessionId,
                            Pcm16 = ByteString.CopyFrom(frame.AudioPcm16.Span),
                        }, state.RelayCancellation.Token);
                        continue;
                    }

                    if (frame.Control == null)
                        continue;

                    await DispatchAsync(new VoiceRemoteControlInputReceived
                    {
                        SessionId = state.SessionId,
                        ControlFrame = frame.Control.Clone(),
                    }, state.RelayCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (state.RelayCancellation.IsCancellationRequested)
            {
            }
            catch
            {
            }
            finally
            {
                await ReleaseStateAsync(state, dispatchCloseRequest: true, awaitRelayCompletion: false);
            }
        }

        private async Task HandleOutputAsync(VoiceRemoteTransportOutput output)
        {
            var state = TryGetState();
            if (state == null ||
                !string.Equals(output.ModuleName, _moduleName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(output.SessionId, state.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            switch (output.OutputCase)
            {
                case VoiceRemoteTransportOutput.OutputOneofCase.AudioOutput:
                    try
                    {
                        await state.Transport.SendAudioAsync(output.AudioOutput.Pcm16.Memory, CancellationToken.None);
                    }
                    catch
                    {
                        await ReleaseStateAsync(state, dispatchCloseRequest: true, awaitRelayCompletion: false);
                    }

                    break;
                case VoiceRemoteTransportOutput.OutputOneofCase.SessionClosed:
                    await ReleaseStateAsync(state, dispatchCloseRequest: false, awaitRelayCompletion: false);
                    break;
                case VoiceRemoteTransportOutput.OutputOneofCase.None:
                default:
                    break;
            }
        }

        private async Task ReleaseStateAsync(
            AttachmentState state,
            bool dispatchCloseRequest,
            bool awaitRelayCompletion)
        {
            var release = false;
            lock (_gate)
            {
                if (ReferenceEquals(_state, state))
                {
                    _state = null;
                    release = true;
                }
            }

            if (!release)
                return;

            state.RelayCancellation.Cancel();

            if (dispatchCloseRequest)
                await DispatchCloseRequestAsync(state.SessionId, "host_detach");

            if (awaitRelayCompletion && state.RelayTask != null)
            {
                try
                {
                    await state.RelayTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await state.Subscription.DisposeAsync();
            await state.Transport.DisposeAsync();
            state.RelayCancellation.Dispose();
        }

        private Task DispatchAsync(IMessage message, CancellationToken ct) =>
            _dispatchPort.DispatchAsync(
                _actorId,
                VoicePresenceSessionDispatch.BuildDirectEnvelope(_actorId, _moduleName, message),
                ct);

        private async Task DispatchCloseRequestAsync(string? sessionId, string reason)
        {
            try
            {
                await DispatchAsync(
                    new VoiceRemoteSessionCloseRequested
                    {
                        SessionId = sessionId ?? string.Empty,
                        Reason = reason,
                    },
                    CancellationToken.None);
            }
            catch
            {
                // cleanup is best-effort after transport shutdown
            }
        }

        private AttachmentState? TryGetState()
        {
            lock (_gate)
            {
                return _state;
            }
        }

        private sealed class AttachmentState(
            string sessionId,
            IVoiceTransport transport,
            CancellationTokenSource relayCancellation,
            IAsyncDisposable subscription)
        {
            public string SessionId { get; } = sessionId;

            public IVoiceTransport Transport { get; } = transport;

            public CancellationTokenSource RelayCancellation { get; } = relayCancellation;

            public IAsyncDisposable Subscription { get; } = subscription;

            public Task? RelayTask { get; set; }
        }
    }
}
