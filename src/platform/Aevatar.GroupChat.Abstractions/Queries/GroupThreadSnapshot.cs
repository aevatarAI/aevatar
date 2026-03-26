namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record GroupThreadSnapshot(
    string ActorId,
    string GroupId,
    string ThreadId,
    string DisplayName,
    IReadOnlyList<string> ParticipantAgentIds,
    IReadOnlyList<GroupParticipantRuntimeBindingSnapshot> ParticipantRuntimeBindings,
    IReadOnlyList<GroupTimelineMessageSnapshot> Messages,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
