namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host voice session capability had no named projection scope because it came from runtime shape.
//   New principle: voice capability read models use an explicit durable materialization projection kind.
public static class VoicePresenceProjectionKinds
{
    public const string CapabilityMaterialization = "voice-presence.capability";
}
