using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.Mapping;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="StudioMemberState"/> committed events into
/// <see cref="StudioMemberCurrentStateDocument"/>. Surfaces a fully-typed
/// projection of the authority — wire-stable string enums, denormalized
/// implementation_ref, denormalized last_binding — so the query port never
/// has to <see cref="Any.Unpack"/> the actor's internal state.
/// </summary>
public sealed class StudioMemberCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StudioMemberCurrentStateProjector(
        IProjectionWriteDispatcher<StudioMemberCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioMemberState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new StudioMemberCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            MemberId = state.MemberId,
            ScopeId = state.ScopeId,
            DisplayName = state.DisplayName,
            Description = state.Description,
            ImplementationKind = MemberImplementationKindMapper.ToWireName(state.ImplementationKind),
            LifecycleStage = MemberImplementationKindMapper.ToWireName(state.LifecycleStage),
            PublishedServiceId = state.PublishedServiceId,
            CreatedAt = state.CreatedAtUtc,
        };

        ApplyImplementationRef(document, state.ImplementationRef);
        ApplyLastBinding(document, state.LastBinding);

        // Team membership (ADR-0017). Mirror the actor's optional team_id
        // into the document — absence means "unassigned" on both the actor
        // and the read model side.
        if (state.HasTeamId)
        {
            document.TeamId = state.TeamId;
        }

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static void ApplyImplementationRef(
        StudioMemberCurrentStateDocument document,
        StudioMemberImplementationRef? implementationRef)
    {
        if (implementationRef == null)
            return;

        if (implementationRef.Workflow != null)
        {
            document.ImplementationWorkflowId = implementationRef.Workflow.WorkflowId ?? string.Empty;
            document.ImplementationWorkflowRevision = implementationRef.Workflow.WorkflowRevision ?? string.Empty;
        }

        if (implementationRef.Script != null)
        {
            document.ImplementationScriptId = implementationRef.Script.ScriptId ?? string.Empty;
            document.ImplementationScriptRevision = implementationRef.Script.ScriptRevision ?? string.Empty;
        }

        if (implementationRef.Gagent != null)
        {
            document.ImplementationActorTypeName = implementationRef.Gagent.ActorTypeName ?? string.Empty;
        }
    }

    private static void ApplyLastBinding(
        StudioMemberCurrentStateDocument document,
        StudioMemberBindingContract? lastBinding)
    {
        if (lastBinding == null || string.IsNullOrEmpty(lastBinding.PublishedServiceId))
            return;

        document.LastBoundPublishedServiceId = lastBinding.PublishedServiceId;
        document.LastBoundRevisionId = lastBinding.RevisionId ?? string.Empty;
        document.LastBoundImplementationKind = MemberImplementationKindMapper.ToWireName(
            lastBinding.ImplementationKind);
        if (lastBinding.BoundAtUtc != null)
            document.LastBoundAt = lastBinding.BoundAtUtc;
    }
}
