namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record GroupTimelineMessageSnapshot(
    string MessageId,
    long TimelineCursor,
    GroupMessageSenderKind SenderKind,
    string SenderId,
    string Text,
    string ReplyToMessageId,
    IReadOnlyList<string> DirectHintAgentIds,
    string TopicId = "",
    GroupSignalKind SignalKind = GroupSignalKind.Unspecified,
    IReadOnlyList<GroupSourceRef>? SourceRefs = null,
    IReadOnlyList<GroupEvidenceRef>? EvidenceRefs = null,
    IReadOnlyList<string>? DerivedFromSignalIds = null);
