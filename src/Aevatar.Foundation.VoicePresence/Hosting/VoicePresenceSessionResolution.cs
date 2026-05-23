namespace Aevatar.Foundation.VoicePresence.Hosting;

// Refactor (iter51/issue-888-voice-presence-lease-ack-snapshot):
//   Old pattern: lease ACK returned VoicePresenceSession bound to pre-lease capability snapshot; endpoint accept/reject closed over stale transport facts.
//   New principle: lease ACK only signals inbox receipt; attach readiness is a separate signal; resolver preflights capability and returns typed sentinel (Unsupported/PreflightFailed/PendingAttach/Attached); endpoint maps typed sentinel, not boolean closure.
public sealed record VoicePresenceSessionResolution(
    VoicePresenceSessionResolutionKind Kind,
    VoicePresenceSession? Session = null,
    VoicePresencePreflightFailureKind? PreflightFailure = null,
    long ObservedStateVersion = 0)
{
    public static VoicePresenceSessionResolution Unsupported(long observedStateVersion = 0) =>
        new(VoicePresenceSessionResolutionKind.Unsupported, ObservedStateVersion: observedStateVersion);

    public static VoicePresenceSessionResolution PreflightFailed(
        VoicePresencePreflightFailureKind failure,
        long observedStateVersion = 0) =>
        new(
            VoicePresenceSessionResolutionKind.PreflightFailed,
            PreflightFailure: failure,
            ObservedStateVersion: observedStateVersion);

    public static VoicePresenceSessionResolution LeaseAcceptedPendingAttach(
        VoicePresenceSession session,
        long observedStateVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(
            VoicePresenceSessionResolutionKind.LeaseAcceptedPendingAttach,
            session,
            ObservedStateVersion: observedStateVersion);
    }

    public static VoicePresenceSessionResolution LeaseAcceptedAttached(
        VoicePresenceSession session,
        long observedStateVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(
            VoicePresenceSessionResolutionKind.LeaseAcceptedAttached,
            session,
            ObservedStateVersion: observedStateVersion);
    }
}

public enum VoicePresenceSessionResolutionKind
{
    Unsupported = 0,
    PreflightFailed = 1,
    LeaseAcceptedPendingAttach = 2,
    LeaseAcceptedAttached = 3,
}

public enum VoicePresencePreflightFailureKind
{
    NotFound = 0,
    NotInitialized = 1,
    TransportAlreadyAttached = 2,
}
