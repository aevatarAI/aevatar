namespace Aevatar.GroupChat.Abstractions.Participants;

public sealed record ParticipantRuntimeDispatchResult(
    string RootActorId,
    string SessionId);
