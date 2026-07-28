using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class WorkOrderCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<WorkOrderCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkOrderCurrentStateProjector(
        IProjectionWriteDispatcher<WorkOrderCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<WorkOrderState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var document = CreateDocument(
            context.RootActorId,
            stateEvent,
            state,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow));

        AddArtifacts(document.InputArtifacts, state.Input?.InputArtifacts);
        AddArtifacts(document.DeclaredResultArtifacts, state.Input?.DeclaredResultArtifacts);
        ApplyRun(document, state.Run);
        ApplyTerminalState(document, state);
        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static WorkOrderCurrentStateDocument CreateDocument(
        string actorId,
        StateEvent stateEvent,
        WorkOrderState state,
        DateTimeOffset projectionObservedAt) =>
        new()
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(projectionObservedAt),
            WorkOrderUpdatedAtUtc = state.UpdatedAtUtc?.Clone(),
            WorkOrderId = state.WorkOrderId,
            DedupKey = state.DedupKey,
            ScopeId = state.ScopeId,
            TeamId = state.TeamId,
            RequesterPrincipalId = state.Requester?.PrincipalId ?? string.Empty,
            RequesterPrincipalKind = state.Requester?.PrincipalKind ?? string.Empty,
            MemberId = state.MemberId,
            PublishedServiceId = state.PublishedServiceId,
            WorkflowId = state.WorkflowId,
            ServiceRevisionId = state.ServiceRevisionId,
            ImplementationKind = state.ImplementationKind,
            EndpointId = state.EndpointId,
            Intent = state.Intent,
            LifecycleStatus = ToWireName(state.LifecycleStatus),
            LifecycleVersion = state.LifecycleVersion,
            CreatedAtUnixMs = ToUnixTimeMilliseconds(state.CreatedAtUtc),
            TimeoutAtUnixMs = ToUnixTimeMilliseconds(state.TimeoutAtUtc),
            InputPrompt = state.Input?.Chat?.Prompt ?? string.Empty,
        };

    private static void ApplyRun(
        WorkOrderCurrentStateDocument document,
        WorkOrderRunLink? run)
    {
        document.RunId = run?.RunId ?? string.Empty;
        document.RunActorId = run?.RunActorId ?? string.Empty;
        document.RunCommandId = run?.CommandId ?? string.Empty;
        document.RunCorrelationId = run?.CorrelationId ?? string.Empty;
        document.RunRevisionId = run?.RevisionId ?? string.Empty;
        document.RunDeploymentId = run?.DeploymentId ?? string.Empty;
        document.RunAcceptedAtUnixMs = ToUnixTimeMilliseconds(run?.AcceptedAtUtc);
    }

    private static void ApplyTerminalState(
        WorkOrderCurrentStateDocument document,
        WorkOrderState state)
    {
        document.RunOutcome = ToRunOutcome(state.RunOutcome);
        document.LateRunOutcome = ToRunOutcome(state.LateRunOutcome);
        document.FailureCode = state.Failure?.Code ?? string.Empty;
        document.FailureMessage = state.Failure?.Message ?? string.Empty;
        document.FailureSource = state.Failure?.Source ?? string.Empty;
        document.FailureReferenceId = state.Failure?.ReferenceId ?? string.Empty;
        document.TerminalReason = state.TerminalReason;
    }

    private static void AddArtifacts(
        ICollection<WorkOrderArtifactReferenceDocument> destination,
        IEnumerable<WorkOrderArtifactReference>? source)
    {
        if (source == null)
            return;
        foreach (var artifact in source)
            destination.Add(ToArtifact(artifact));
    }

    private static WorkOrderArtifactReferenceDocument ToArtifact(WorkOrderArtifactReference artifact) =>
        new()
        {
            ArtifactId = artifact.ArtifactId,
            ArtifactKind = artifact.ArtifactKind,
            Uri = artifact.Uri,
            RevisionId = artifact.RevisionId,
        };

    private static WorkOrderRunOutcomeReferenceDocument? ToRunOutcome(
        WorkOrderRunOutcomeReference? outcome)
    {
        if (outcome == null || string.IsNullOrWhiteSpace(outcome.DeliveryId))
            return null;

        return new WorkOrderRunOutcomeReferenceDocument
        {
            DeliveryId = outcome.DeliveryId,
            RunId = outcome.RunId,
            RunActorId = outcome.RunActorId,
            CommandId = outcome.CommandId,
            CorrelationId = outcome.CorrelationId,
            Outcome = outcome.Outcome switch
            {
                WorkOrderTerminalOutcome.Succeeded => "succeeded",
                WorkOrderTerminalOutcome.Failed => "failed",
                WorkOrderTerminalOutcome.Stopped => "stopped",
                _ => string.Empty,
            },
            TerminalAtUnixMs = ToUnixTimeMilliseconds(outcome.TerminalAtUtc),
        };
    }

    private static string ToWireName(WorkOrderLifecycleStatus status) => status switch
    {
        WorkOrderLifecycleStatus.Accepted => WorkOrderLifecycleStatusNames.Accepted,
        WorkOrderLifecycleStatus.Ready => WorkOrderLifecycleStatusNames.Ready,
        WorkOrderLifecycleStatus.DispatchPending => WorkOrderLifecycleStatusNames.DispatchPending,
        WorkOrderLifecycleStatus.Running => WorkOrderLifecycleStatusNames.Running,
        WorkOrderLifecycleStatus.Completed => WorkOrderLifecycleStatusNames.Completed,
        WorkOrderLifecycleStatus.Failed => WorkOrderLifecycleStatusNames.Failed,
        WorkOrderLifecycleStatus.Stopped => WorkOrderLifecycleStatusNames.Stopped,
        WorkOrderLifecycleStatus.Cancelled => WorkOrderLifecycleStatusNames.Cancelled,
        WorkOrderLifecycleStatus.TimedOut => WorkOrderLifecycleStatusNames.TimedOut,
        _ => string.Empty,
    };

    private static long ToUnixTimeMilliseconds(Timestamp? timestamp) =>
        timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0;
}
