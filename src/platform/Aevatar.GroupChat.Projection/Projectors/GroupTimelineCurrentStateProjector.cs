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
            .Select(static binding =>
            {
                var readModel = new GroupParticipantRuntimeBindingReadModel
                {
                    ParticipantAgentId = binding.ParticipantAgentId,
                    TargetKindValue = (int)binding.TargetKind,
                };

                if (binding.ServiceTarget != null)
                {
                    readModel.ServiceTarget = new GroupServiceRuntimeTargetReadModel
                    {
                        TenantId = binding.ServiceTarget.TenantId,
                        AppId = binding.ServiceTarget.AppId,
                        Namespace = binding.ServiceTarget.Namespace,
                        ServiceId = binding.ServiceTarget.ServiceId,
                        EndpointId = binding.ServiceTarget.EndpointId,
                        ScopeId = binding.ServiceTarget.ScopeId,
                    };
                }
                else if (binding.WorkflowTarget != null)
                {
                    readModel.WorkflowTarget = new GroupWorkflowRuntimeTargetReadModel
                    {
                        DefinitionActorId = binding.WorkflowTarget.DefinitionActorId,
                        WorkflowName = binding.WorkflowTarget.WorkflowName,
                        ScopeId = binding.WorkflowTarget.ScopeId,
                    };
                }
                else if (binding.ScriptTarget != null)
                {
                    readModel.ScriptTarget = new GroupScriptRuntimeTargetReadModel
                    {
                        DefinitionActorId = binding.ScriptTarget.DefinitionActorId,
                        Revision = binding.ScriptTarget.Revision,
                        RuntimeActorId = binding.ScriptTarget.RuntimeActorId,
                        RequestedEventType = binding.ScriptTarget.RequestedEventType,
                        ScopeId = binding.ScriptTarget.ScopeId,
                    };
                }
                else if (binding.LocalTarget != null)
                {
                    readModel.LocalTarget = new GroupLocalRuntimeTargetReadModel
                    {
                        Provider = binding.LocalTarget.Provider,
                    };
                }

                return readModel;
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
