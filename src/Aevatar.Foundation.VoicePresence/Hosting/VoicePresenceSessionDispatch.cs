using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Hosting;

internal static class VoicePresenceSessionDispatch
{
    public const string HostPublisherId = "voice-presence.host";

    public static EventEnvelope BuildSelfEnvelope(
        string actorId,
        string moduleName,
        IMessage message) =>
        BuildEnvelope(
            actorId,
            CreateModuleSignal(moduleName, message),
            EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self));

    public static EventEnvelope BuildDirectEnvelope(
        string actorId,
        string moduleName,
        IMessage message) =>
        BuildEnvelope(
            actorId,
            CreateModuleSignal(moduleName, message),
            EnvelopeRouteSemantics.CreateDirect(HostPublisherId, actorId));

    private static EventEnvelope BuildEnvelope(
        string actorId,
        VoiceModuleSignal signal,
        EnvelopeRoute route) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(signal),
            Route = route,
        };

    private static VoiceModuleSignal CreateModuleSignal(string moduleName, IMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(message);

        var signal = new VoiceModuleSignal
        {
            ModuleName = moduleName,
        };

        switch (message)
        {
            case VoiceProviderEvent providerEvent:
                signal.ProviderEvent = providerEvent.Clone();
                break;
            case VoiceControlFrame controlFrame:
                signal.ControlFrame = controlFrame.Clone();
                break;
            case VoiceRemoteSessionOpenRequested openRequested:
                signal.RemoteSessionOpenRequested = openRequested.Clone();
                break;
            case VoiceRemoteSessionCloseRequested closeRequested:
                signal.RemoteSessionCloseRequested = closeRequested.Clone();
                break;
            case VoiceRemoteControlInputReceived controlInput:
                signal.RemoteControlInputReceived = controlInput.Clone();
                break;
            case VoicePresenceSessionLeaseRequested leaseRequested:
                signal.SessionLeaseRequested = leaseRequested.Clone();
                break;
            case VoicePresenceSessionLeaseReleased leaseReleased:
                signal.SessionLeaseReleased = leaseReleased.Clone();
                break;
            case VoiceTransportAttachRequested attachRequested:
                signal.TransportAttachRequested = attachRequested.Clone();
                break;
            case VoiceTransportDetachRequested detachRequested:
                signal.TransportDetachRequested = detachRequested.Clone();
                break;
            case VoiceTransportControlFrameReceived controlReceived:
                signal.TransportControlFrameReceived = controlReceived.Clone();
                break;
            case VoiceTransportRelayStopped relayStopped:
                signal.TransportRelayStopped = relayStopped.Clone();
                break;
            case VoiceTransportLifetimeCompleted lifetimeCompleted:
                signal.TransportLifetimeCompleted = lifetimeCompleted.Clone();
                break;
            case VoiceProviderEventReceived providerReceived:
                signal.ProviderEventReceived = providerReceived.Clone();
                break;
            case VoiceTransportAudioFrameReceived audioReceived:
                signal.TransportAudioFrameReceived = audioReceived.Clone();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported voice module signal payload '{message.GetType().Name}'.");
        }

        return signal;
    }
}
