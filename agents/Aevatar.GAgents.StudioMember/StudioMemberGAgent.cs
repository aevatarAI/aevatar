using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Per-member actor that owns the canonical StudioMember authority state.
///
/// Actor ID convention: <c>studio-member:{scopeId}:{memberId}</c>.
/// The actor is the only writer of <c>published_service_id</c>, which is
/// generated once at creation from the immutable <c>member_id</c> and never
/// recomputed on rename.
/// </summary>
public sealed class StudioMemberGAgent : GAgentBase<StudioMemberState>, IProjectedActor
{
    public static string ProjectionKind => "studio-member";

    [EventHandler(EndpointName = "createMember")]
    public async Task HandleCreated(StudioMemberCreatedEvent evt)
    {
        if (!string.IsNullOrEmpty(State.MemberId))
        {
            // Idempotent re-create with same identity is a no-op; otherwise reject
            // so a stray duplicate cannot overwrite an existing member's
            // publishedServiceId or scope binding.
            if (!string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"member already initialized with id '{State.MemberId}'.");
            }

            return;
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "renameMember")]
    public async Task HandleRenamed(StudioMemberRenamedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "updateImplementation")]
    public async Task HandleImplementationUpdated(StudioMemberImplementationUpdatedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "recordBinding")]
    public async Task HandleBound(StudioMemberBoundEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    protected override StudioMemberState TransitionState(
        StudioMemberState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StudioMemberCreatedEvent>(ApplyCreated)
            .On<StudioMemberRenamedEvent>(ApplyRenamed)
            .On<StudioMemberImplementationUpdatedEvent>(ApplyImplementationUpdated)
            .On<StudioMemberBoundEvent>(ApplyBound)
            .OrCurrent();
    }

    private static StudioMemberState ApplyCreated(
        StudioMemberState state, StudioMemberCreatedEvent evt)
    {
        return new StudioMemberState
        {
            MemberId = evt.MemberId,
            ScopeId = evt.ScopeId,
            DisplayName = evt.DisplayName,
            Description = evt.Description,
            ImplementationKind = evt.ImplementationKind,
            ImplementationRef = null,
            PublishedServiceId = evt.PublishedServiceId,
            LifecycleStage = StudioMemberLifecycleStage.Created,
            CreatedAtUtc = evt.CreatedAtUtc,
            UpdatedAtUtc = evt.CreatedAtUtc,
            LastBinding = null,
        };
    }

    private static StudioMemberState ApplyRenamed(
        StudioMemberState state, StudioMemberRenamedEvent evt)
    {
        var next = state.Clone();
        next.DisplayName = evt.DisplayName;
        next.Description = evt.Description;
        next.UpdatedAtUtc = evt.UpdatedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyImplementationUpdated(
        StudioMemberState state, StudioMemberImplementationUpdatedEvent evt)
    {
        var next = state.Clone();
        next.ImplementationKind = evt.ImplementationKind;
        next.ImplementationRef = evt.ImplementationRef?.Clone();
        next.UpdatedAtUtc = evt.UpdatedAtUtc;
        if (next.LifecycleStage == StudioMemberLifecycleStage.Created
            && HasResolvedImplementationRef(evt.ImplementationRef))
        {
            next.LifecycleStage = StudioMemberLifecycleStage.BuildReady;
        }

        return next;
    }

    private static StudioMemberState ApplyBound(
        StudioMemberState state, StudioMemberBoundEvent evt)
    {
        var next = state.Clone();
        next.LastBinding = new StudioMemberBindingContract
        {
            PublishedServiceId = evt.PublishedServiceId,
            RevisionId = evt.RevisionId,
            ImplementationKind = evt.ImplementationKind,
            BoundAtUtc = evt.BoundAtUtc,
        };
        next.LifecycleStage = StudioMemberLifecycleStage.BindReady;
        next.UpdatedAtUtc = evt.BoundAtUtc;
        return next;
    }

    private static bool HasResolvedImplementationRef(StudioMemberImplementationRef? implRef)
    {
        if (implRef == null)
            return false;

        if (implRef.Workflow != null && !string.IsNullOrEmpty(implRef.Workflow.WorkflowId))
            return true;
        if (implRef.Script != null && !string.IsNullOrEmpty(implRef.Script.ScriptId))
            return true;
        if (implRef.Gagent != null && !string.IsNullOrEmpty(implRef.Gagent.ActorTypeName))
            return true;

        return false;
    }
}
