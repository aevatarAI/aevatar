using Aevatar.GroupChat.Abstractions.Queries;

namespace Aevatar.GroupChat.Abstractions.Participants;

public sealed record ParticipantRuntimeDispatchRequest(
    string GroupId,
    string ThreadId,
    string ParticipantAgentId,
    string SourceEventId,
    long SourceStateVersion,
    long TimelineCursor,
    GroupTimelineMessageSnapshot TriggerMessage,
    GroupThreadSnapshot Thread,
    GroupParticipantRuntimeBindingSnapshot Binding);
