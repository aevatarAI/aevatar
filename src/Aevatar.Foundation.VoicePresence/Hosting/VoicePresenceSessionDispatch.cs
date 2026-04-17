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
            case VoiceRemoteAudioInputReceived audioInput:
                signal.RemoteAudioInputReceived = audioInput.Clone();
                break;
            case VoiceRemoteControlInputReceived controlInput:
                signal.RemoteControlInputReceived = controlInput.Clone();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported voice module signal payload '{message.GetType().Name}'.");
        }

        return signal;
    }
}
