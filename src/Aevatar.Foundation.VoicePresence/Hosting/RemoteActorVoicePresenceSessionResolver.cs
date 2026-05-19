using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Resolves runtime-neutral host sessions that dispatch remote voice setup/control messages.
/// </summary>
// Refactor (iter15/cluster-025-voice-host-session-state-actorization):
//   Old pattern: voice host resolver locks shared mutable lease state outside actor lifecycle
//   New principle: actor owns remote session identity; host dispatches setup/control only.
//   Remote media attach fails until a non-envelope raw audio transport exists.
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
        if (actorRuntime == null || dispatchPort == null)
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
            dispatchPort);

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

        public RemoteActorVoicePresenceSessionBridge(
            string actorId,
            string moduleName,
            int pcmSampleRateHz,
            IActorDispatchPort dispatchPort)
        {
            _actorId = actorId;
            _moduleName = moduleName;
            _pcmSampleRateHz = pcmSampleRateHz;
            _dispatchPort = dispatchPort;
        }

        public VoicePresenceSession CreateSession() =>
            new(
                isInitialized: static () => true,
                isTransportAttached: static () => false,
                attachTransportAsync: AttachTransportAsync,
                detachTransportAsync: DetachTransportAsync,
                pcmSampleRateHz: _pcmSampleRateHz);

        // Refactor (iter15/cluster-025-voice-host-session-state-actorization):
        //   Old pattern: voice host resolver locks shared mutable lease state outside actor lifecycle
        //   New principle: remote attach keeps setup/control envelopes but rejects PCM transport.
        //   Chunks never cross EventEnvelope; audio waits for a raw transport.
        private async Task AttachTransportAsync(IVoiceTransport transport, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ct.ThrowIfCancellationRequested();

            var sessionId = Guid.NewGuid().ToString("N");
            try
            {
                await DispatchAsync(new VoiceRemoteSessionOpenRequested
                {
                    SessionId = sessionId,
                }, ct);

                await DispatchCloseRequestAsync(
                    sessionId,
                    VoiceRemoteAudioTransportUnavailableException.Reason,
                    ct);
            }
            finally
            {
                await transport.DisposeAsync();
            }

            throw new VoiceRemoteAudioTransportUnavailableException();
        }

        // Refactor (iter15/cluster-025-voice-host-session-state-actorization):
        //   Old pattern: detach released host-held attachment state, subscription, and relay task.
        //   New principle: host has no attachment fact to release; detach only sends best-effort actor close.
        private async Task DetachTransportAsync(IVoiceTransport? expectedTransport, CancellationToken ct)
        {
            _ = expectedTransport;
            await DispatchCloseRequestAsync(sessionId: null, "host_detach", ct);
        }

        private Task DispatchAsync(IMessage message, CancellationToken ct) =>
            _dispatchPort.DispatchAsync(
                _actorId,
                VoicePresenceSessionDispatch.BuildDirectEnvelope(_actorId, _moduleName, message),
                ct);

        private async Task DispatchCloseRequestAsync(string? sessionId, string reason, CancellationToken ct)
        {
            try
            {
                await DispatchAsync(
                    new VoiceRemoteSessionCloseRequested
                    {
                        SessionId = sessionId ?? string.Empty,
                        Reason = reason,
                    },
                    ct);
            }
            catch
            {
                // cleanup is best-effort after transport shutdown
            }
        }
    }
}
