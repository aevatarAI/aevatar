using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Projection.Configuration;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Queries;

public sealed class GroupTimelineQueryPort : IGroupTimelineQueryPort
{
    private readonly IProjectionDocumentReader<GroupTimelineReadModel, string> _documentReader;
    private readonly bool _enabled;

    public GroupTimelineQueryPort(
        IProjectionDocumentReader<GroupTimelineReadModel, string> documentReader,
        GroupChatProjectionOptions? options = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<GroupThreadSnapshot?> GetThreadAsync(
        string groupId,
        string threadId,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        var readModel = await _documentReader.GetAsync(GroupChatActorIds.Thread(groupId, threadId), ct);
        return readModel == null ? null : MapThread(readModel);
    }

    public async Task<IReadOnlyList<GroupTimelineMessageSnapshot>> GetMentionedMessagesAsync(
        string groupId,
        string threadId,
        string participantAgentId,
        long sinceCursor = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return [];

        ArgumentException.ThrowIfNullOrWhiteSpace(participantAgentId);
        var boundedTake = Math.Clamp(take, 1, 500);
        var readModel = await _documentReader.GetAsync(GroupChatActorIds.Thread(groupId, threadId), ct);
        if (readModel == null)
            return [];

        return readModel.Messages
            .Where(x => x.TimelineCursor > sinceCursor)
            .Where(x => x.DirectHintAgentIds.Contains(participantAgentId))
            .OrderBy(x => x.TimelineCursor)
            .Take(boundedTake)
            .Select(MapMessage)
            .ToList();
    }

    private static GroupThreadSnapshot MapThread(GroupTimelineReadModel readModel)
    {
        return new GroupThreadSnapshot(
            readModel.ActorId,
            readModel.GroupId,
            readModel.ThreadId,
            readModel.DisplayName,
            [.. readModel.ParticipantAgentIds],
            readModel.ParticipantRuntimeBindings
                .Select(x => new GroupParticipantRuntimeBindingSnapshot(
                    x.ParticipantAgentId,
                    Enum.IsDefined(typeof(GroupParticipantRuntimeTargetKind), x.TargetKindValue)
                        ? (GroupParticipantRuntimeTargetKind)x.TargetKindValue
                        : GroupParticipantRuntimeTargetKind.Unspecified,
                    x.ServiceTarget == null
                        ? null
                        : new GroupServiceRuntimeTargetSnapshot(
                            x.ServiceTarget.TenantId,
                            x.ServiceTarget.AppId,
                            x.ServiceTarget.Namespace,
                            x.ServiceTarget.ServiceId,
                            x.ServiceTarget.EndpointId,
                            x.ServiceTarget.ScopeId),
                    x.WorkflowTarget == null
                        ? null
                        : new GroupWorkflowRuntimeTargetSnapshot(
                            x.WorkflowTarget.DefinitionActorId,
                            x.WorkflowTarget.WorkflowName,
                            x.WorkflowTarget.ScopeId),
                    x.ScriptTarget == null
                        ? null
                        : new GroupScriptRuntimeTargetSnapshot(
                            x.ScriptTarget.DefinitionActorId,
                            x.ScriptTarget.Revision,
                            x.ScriptTarget.RuntimeActorId,
                            x.ScriptTarget.RequestedEventType,
                            x.ScriptTarget.ScopeId),
                    x.LocalTarget == null
                        ? null
                        : new GroupLocalRuntimeTargetSnapshot(x.LocalTarget.Provider)))
                .ToList(),
            readModel.Messages.Select(MapMessage).ToList(),
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.UpdatedAt);
    }

    private static GroupTimelineMessageSnapshot MapMessage(GroupTimelineMessageReadModel readModel)
    {
        var senderKind = Enum.IsDefined(typeof(GroupMessageSenderKind), readModel.SenderKindValue)
            ? (GroupMessageSenderKind)readModel.SenderKindValue
            : GroupMessageSenderKind.Unspecified;
        var signalKind = Enum.IsDefined(typeof(GroupSignalKind), readModel.SignalKindValue)
            ? (GroupSignalKind)readModel.SignalKindValue
            : GroupSignalKind.Unspecified;
        return new GroupTimelineMessageSnapshot(
            readModel.MessageId,
            readModel.TimelineCursor,
            senderKind,
            readModel.SenderId,
            readModel.Text,
            readModel.ReplyToMessageId,
            [.. readModel.DirectHintAgentIds],
            readModel.TopicId,
            signalKind,
            readModel.SourceRefs
                .Select(static sourceRef => new GroupSourceRef
                {
                    SourceKind = Enum.IsDefined(typeof(GroupSourceKind), sourceRef.SourceKindValue)
                        ? (GroupSourceKind)sourceRef.SourceKindValue
                        : GroupSourceKind.Unspecified,
                    Locator = sourceRef.Locator,
                    SourceId = sourceRef.SourceId,
                })
                .ToList(),
            readModel.EvidenceRefs
                .Select(static evidenceRef => new GroupEvidenceRef
                {
                    EvidenceId = evidenceRef.EvidenceId,
                    SourceLocator = evidenceRef.SourceLocator,
                    Locator = evidenceRef.Locator,
                    ExcerptSummary = evidenceRef.ExcerptSummary,
                    SourceId = evidenceRef.SourceId,
                })
                .ToList(),
            [.. readModel.DerivedFromSignalIds]);
    }
}
