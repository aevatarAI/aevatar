namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record AgentFeedSnapshot(
    string ActorId,
    string AgentId,
    long FeedCursor,
    IReadOnlyList<AgentFeedItemSnapshot> NextItems,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
