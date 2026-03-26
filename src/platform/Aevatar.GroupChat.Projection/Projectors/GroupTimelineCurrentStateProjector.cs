using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Core.GAgents;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Projectors;

public sealed class GroupTimelineCurrentStateProjector
    : ICurrentStateProjectionMaterializer<GroupTimelineProjectionContext>
{
    private readonly IProjectionWriteDispatcher<GroupTimelineReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public GroupTimelineCurrentStateProjector(
        IProjectionWriteDispatcher<GroupTimelineReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        GroupTimelineProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<GroupThreadState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new GroupTimelineReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            GroupId = state.GroupId,
            ThreadId = state.ThreadId,
            DisplayName = state.DisplayName,
        };
        document.ParticipantAgentIds = [.. state.ParticipantAgentIds];
        document.ParticipantRuntimeBindings = state.ParticipantRuntimeBindingEntries
            .Select(static binding => new GroupParticipantRuntimeBindingReadModel
            {
                ParticipantAgentId = binding.ParticipantAgentId,
                TenantId = binding.TenantId,
                AppId = binding.AppId,
                Namespace = binding.Namespace,
                ServiceId = binding.ServiceId,
                EndpointId = binding.EndpointId,
                ScopeId = binding.ScopeId,
            })
            .ToList();
        document.Messages = state.MessageEntries
            .Select(static message =>
            {
                var readModel = new GroupTimelineMessageReadModel
                {
                    MessageId = message.MessageId,
                    TimelineCursor = message.TimelineCursor,
                    SenderKindValue = (int)message.SenderKind,
                    SenderId = message.SenderId,
                    Text = message.Text,
                    ReplyToMessageId = message.ReplyToMessageId,
                    TopicId = message.TopicId,
                    SignalKindValue = (int)message.SignalKind,
                };
                readModel.SourceRefs = message.SourceRefs
                    .Select(static sourceRef => new GroupTimelineSourceRefReadModel
                    {
                        SourceKindValue = (int)sourceRef.SourceKind,
                        Locator = sourceRef.Locator,
                        SourceId = sourceRef.SourceId,
                    })
                    .ToList();
                readModel.EvidenceRefs = message.EvidenceRefs
                    .Select(static evidenceRef => new GroupTimelineEvidenceRefReadModel
                    {
                        EvidenceId = evidenceRef.EvidenceId,
                        SourceLocator = evidenceRef.SourceLocator,
                        Locator = evidenceRef.Locator,
                        ExcerptSummary = evidenceRef.ExcerptSummary,
                        SourceId = evidenceRef.SourceId,
                    })
                    .ToList();
                readModel.DerivedFromSignalIds = [.. message.DerivedFromSignalIds];
                readModel.DirectHintAgentIds = [.. message.DirectHintAgentIds];
                return readModel;
            })
            .ToList();

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
