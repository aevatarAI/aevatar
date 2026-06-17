using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed record VoiceRealtimeSessionRequest(
    string ActorId,
    string? ModuleName = null,
    VoiceRealtimeSessionPurpose Purpose = VoiceRealtimeSessionPurpose.Attach,
    VoiceSessionOverrides? SessionOverrides = null,
    VoiceToolExecutionContext? ToolContext = null);

public enum VoiceRealtimeSessionPurpose
{
    Attach = 0,
    Detach = 1,
}

public sealed record VoiceRealtimeSessionAccepted(
    string ActorId,
    string ModuleName,
    string SessionId,
    int PcmSampleRateHz,
    long ObservedStateVersion,
    VoicePresenceSessionLeaseHandle LeaseHandle);

public enum VoiceRealtimeSessionStartError
{
    None = 0,
    Unsupported = 1,
    NotFound = 2,
    NotInitialized = 3,
    TransportAlreadyAttached = 4,
}

public enum VoiceRealtimeSessionCompletion
{
    Accepted = 0,
}
