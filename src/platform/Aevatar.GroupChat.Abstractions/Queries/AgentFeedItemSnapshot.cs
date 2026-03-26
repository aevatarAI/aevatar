namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record AgentFeedItemSnapshot(
    string SignalId,
    string GroupId,
    string ThreadId,
    string TopicId,
    GroupMessageSenderKind SenderKind,
    string SenderId,
    GroupSignalKind SignalKind,
    string SourceEventId,
    long SourceStateVersion,
    long TimelineCursor,
    GroupFeedAcceptReason AcceptReason,
    int RankScore);
