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
/// Materializes StudioMemberBindingRunGAgent committed state into the run-owned
/// status read model consumed by the binding-run status API.
/// </summary>
public sealed class StudioMemberBindingRunCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<StudioMemberBindingRunCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StudioMemberBindingRunCurrentStateProjector(
        IProjectionWriteDispatcher<StudioMemberBindingRunCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioMemberBindingRunState>(
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
        var document = new StudioMemberBindingRunCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            BindingRunId = state.BindingRunId,
            ScopeId = state.ScopeId,
            MemberId = state.MemberId,
            RequestHash = state.RequestHash,
            Status = MemberImplementationKindMapper.ToWireName(state.Status),
            PlatformBindingCommandId = state.PlatformBindingCommandId,
            AttemptCount = state.AttemptCount,
        };

        ApplyFailure(document, state.Failure);
        ApplyPlatformResult(document, state.PlatformResult);

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static void ApplyFailure(
        StudioMemberBindingRunCurrentStateDocument document,
        StudioMemberBindingFailure? failure)
    {
        if (failure == null)
            return;

        document.FailureCode = failure.Code ?? string.Empty;
        document.FailureMessage = failure.Message ?? string.Empty;
        document.FailureAt = failure.FailedAtUtc;
    }

    private static void ApplyPlatformResult(
        StudioMemberBindingRunCurrentStateDocument document,
        StudioMemberPlatformBindingResult? result)
    {
        if (result == null)
            return;

        document.ResultPublishedServiceId = result.PublishedServiceId ?? string.Empty;
        document.ResultRevisionId = result.RevisionId ?? string.Empty;
        document.ResultImplementationKind = MemberImplementationKindMapper.ToWireName(result.ImplementationKind);
        document.ResultExpectedActorId = result.ExpectedActorId ?? string.Empty;
    }
}
