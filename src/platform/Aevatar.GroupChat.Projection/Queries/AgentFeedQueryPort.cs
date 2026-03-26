using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Queries;

public sealed class AgentFeedQueryPort : IAgentFeedQueryPort
{
    private readonly IProjectionDocumentReader<AgentFeedReadModel, string> _documentReader;

    public AgentFeedQueryPort(IProjectionDocumentReader<AgentFeedReadModel, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<AgentFeedSnapshot?> GetFeedAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var readModel = await _documentReader.GetAsync(GroupChatActorIds.Feed(agentId), ct);
        return readModel == null ? null : MapFeed(readModel);
    }

    public async Task<AgentFeedItemSnapshot?> GetTopItemAsync(string agentId, CancellationToken ct = default)
    {
        var feed = await GetFeedAsync(agentId, ct);
        return feed?.NextItems.FirstOrDefault();
    }

    private static AgentFeedSnapshot MapFeed(AgentFeedReadModel readModel)
    {
        return new AgentFeedSnapshot(
            readModel.ActorId,
            readModel.AgentId,
            readModel.FeedCursor,
            readModel.NextItems.Select(MapItem).ToList(),
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.UpdatedAt);
    }

    private static AgentFeedItemSnapshot MapItem(AgentFeedItemReadModel readModel)
    {
        var senderKind = Enum.IsDefined(typeof(GroupMessageSenderKind), readModel.SenderKindValue)
            ? (GroupMessageSenderKind)readModel.SenderKindValue
            : GroupMessageSenderKind.Unspecified;
        var signalKind = Enum.IsDefined(typeof(GroupSignalKind), readModel.SignalKindValue)
            ? (GroupSignalKind)readModel.SignalKindValue
            : GroupSignalKind.Unspecified;
        var acceptReason = Enum.IsDefined(typeof(GroupFeedAcceptReason), readModel.AcceptReasonValue)
            ? (GroupFeedAcceptReason)readModel.AcceptReasonValue
            : GroupFeedAcceptReason.Unspecified;
        return new AgentFeedItemSnapshot(
            readModel.SignalId,
            readModel.GroupId,
            readModel.ThreadId,
            readModel.TopicId,
            senderKind,
            readModel.SenderId,
            signalKind,
            readModel.SourceEventId,
            readModel.SourceStateVersion,
            readModel.TimelineCursor,
            acceptReason,
            readModel.RankScore);
    }
}
