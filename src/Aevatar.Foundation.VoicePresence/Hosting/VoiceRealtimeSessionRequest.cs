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
    VoicePresenceSessionLeaseHandle LeaseHandle,
    string WireContractVersion = VoiceWireContractDefaults.CurrentWireContractVersion,
    VoiceInputImagePolicy? InputImagePolicy = null,
    VoiceRealtimeAttachOutcome AttachOutcome = VoiceRealtimeAttachOutcome.NewSession)
{
    public string EffectiveWireContractVersion =>
        string.IsNullOrWhiteSpace(WireContractVersion)
            ? VoiceWireContractDefaults.CurrentWireContractVersion
            : WireContractVersion;

    public VoiceInputImagePolicy CreateEffectiveInputImagePolicy() =>
        InputImagePolicy?.Clone() ?? VoiceWireContractDefaults.CreateInputImagePolicy();
}

public enum VoiceRealtimeAttachOutcome
{
    NewSession = 0,
    Restarted = 1,
}

public enum VoiceRealtimeSessionStartError
{
    None = 0,
    Unsupported = 1,
    NotFound = 2,
    NotInitialized = 3,
    TransportAlreadyAttached = 4,

    // The voice capability grain accepted the lease, but its read-model projection never reflected the
    // committed state within the observation budget (projection lag / stuck read model). A retryable
    // condition — distinct from a genuinely-missing capability (NotFound) — surfaced to the client as a
    // typed 503 instead of an opaque empty-body 500.
    CapabilityNotReady = 5,
}

public enum VoiceRealtimeSessionCompletion
{
    Accepted = 0,
}
