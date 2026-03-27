namespace Aevatar.GroupChat.Abstractions.Participants;

public enum ParticipantRuntimeBackendKind
{
    Unspecified = 0,
    Service = 1,
    Workflow = 2,
    Script = 3,
    Local = 4,
}

public enum ParticipantRuntimeCompletionMode
{
    AsyncObserved = 0,
    SyncCompleted = 1,
    AcceptedNoObservation = 2,
}

public sealed record ParticipantRuntimeDispatchResult(
    ParticipantRuntimeBackendKind BackendKind,
    ParticipantRuntimeCompletionMode CompletionMode,
    string RootActorId,
    string SessionId,
    string ReplyMessageId,
    string? ReplyText = null);
