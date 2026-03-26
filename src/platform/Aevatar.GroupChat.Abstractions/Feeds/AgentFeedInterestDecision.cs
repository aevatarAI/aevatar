namespace Aevatar.GroupChat.Abstractions.Feeds;

public sealed record AgentFeedInterestDecision(
    int Score,
    GroupFeedAcceptReason AcceptReason);
