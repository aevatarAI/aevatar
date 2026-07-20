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
        AddPermissionPlan(document, state.PermissionPlan);
        ApplyApproval(document, state.Approval);
        ApplyExecution(document, state.Execution);
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

    private static void ApplyApproval(
        WorkOrderCurrentStateDocument document,
        WorkOrderApprovalState? approval)
    {
        document.ApprovalId = approval?.ApprovalId ?? string.Empty;
        document.ApprovalStatus = ToWireName(
            approval?.Status ?? WorkOrderApprovalStatus.Unspecified);
        document.ApprovalDecisionId = approval?.DecisionId ?? string.Empty;
        document.ApprovalDecidedById = approval?.DecidedBy?.PrincipalId ?? string.Empty;
        document.ApprovalDecidedByKind = approval?.DecidedBy?.PrincipalKind ?? string.Empty;
        document.ApprovalReason = approval?.Reason ?? string.Empty;
        document.ApprovalDecidedAtUnixMs = ToUnixTimeMilliseconds(approval?.DecidedAtUtc);
    }

    private static void ApplyExecution(
        WorkOrderCurrentStateDocument document,
        WorkOrderExecutionProvenance? execution)
    {
        document.RunId = execution?.RunId ?? string.Empty;
        document.RunActorId = execution?.RunActorId ?? string.Empty;
        document.RunCommandId = execution?.CommandId ?? string.Empty;
        document.RunCorrelationId = execution?.CorrelationId ?? string.Empty;
        document.RunRevisionId = execution?.RevisionId ?? string.Empty;
        document.RunDeploymentId = execution?.DeploymentId ?? string.Empty;
        document.RunAcceptedAtUnixMs = ToUnixTimeMilliseconds(execution?.AcceptedAtUtc);
        document.RunStartedAtUnixMs = ToUnixTimeMilliseconds(execution?.StartedAtUtc);
    }

    private static void ApplyTerminalState(
        WorkOrderCurrentStateDocument document,
        WorkOrderState state)
    {
        document.TerminalEvidence = ToTerminalEvidence(state.TerminalEvidence);
        document.LateTerminalEvidence = ToTerminalEvidence(state.LateTerminalEvidence);
        document.FailureCode = state.Failure?.Code ?? string.Empty;
        document.FailureMessage = state.Failure?.Message ?? string.Empty;
        document.FailureSource = state.Failure?.Source ?? string.Empty;
        document.FailureReferenceId = state.Failure?.ReferenceId ?? string.Empty;
        document.TerminalReason = state.TerminalReason;
    }

    private static void AddPermissionPlan(
        WorkOrderCurrentStateDocument document,
        WorkOrderPermissionPlan? plan)
    {
        if (plan == null)
            return;

        document.ExternalActions.Add(plan.ExternalActions.Select(action =>
            new WorkOrderExternalActionReferenceDocument
            {
                ActionId = action.ActionId,
                System = action.System,
                Action = action.Action,
                ResourceId = action.ResourceId,
            }));
        document.PermissionRequirements.Add(plan.Requirements.Select(requirement =>
            new WorkOrderPermissionRequirementDocument
            {
                PermissionId = requirement.PermissionId,
                ActionId = requirement.ActionId,
                Capability = requirement.Capability,
                RequiresApproval = requirement.RequiresApproval,
            }));
        document.ApproverPrincipalIds.Add(plan.ApproverPrincipalIds);
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

    private static WorkOrderTerminalEvidenceDocument? ToTerminalEvidence(
        WorkOrderTerminalEvidence? evidence)
    {
        if (evidence == null || string.IsNullOrWhiteSpace(evidence.DeliveryId))
            return null;

        var document = new WorkOrderTerminalEvidenceDocument
        {
            DeliveryId = evidence.DeliveryId,
            RunId = evidence.RunId,
            RunActorId = evidence.RunActorId,
            CommandId = evidence.CommandId,
            CorrelationId = evidence.CorrelationId,
            Outcome = evidence.Outcome switch
            {
                WorkOrderTerminalOutcome.Succeeded => "succeeded",
                WorkOrderTerminalOutcome.Failed => "failed",
                WorkOrderTerminalOutcome.Stopped => "stopped",
                _ => string.Empty,
            },
            Output = evidence.Output,
            Error = evidence.Error,
            TerminalAtUnixMs = ToUnixTimeMilliseconds(evidence.TerminalAtUtc),
        };
        document.ResultArtifacts.Add(evidence.ResultArtifacts.Select(ToArtifact));
        return document;
    }

    private static string ToWireName(WorkOrderLifecycleStatus status) => status switch
    {
        WorkOrderLifecycleStatus.Accepted => WorkOrderLifecycleStatusNames.Accepted,
        WorkOrderLifecycleStatus.WaitingApproval => WorkOrderLifecycleStatusNames.WaitingApproval,
        WorkOrderLifecycleStatus.Ready => WorkOrderLifecycleStatusNames.Ready,
        WorkOrderLifecycleStatus.DispatchPending => WorkOrderLifecycleStatusNames.DispatchPending,
        WorkOrderLifecycleStatus.Running => WorkOrderLifecycleStatusNames.Running,
        WorkOrderLifecycleStatus.Completed => WorkOrderLifecycleStatusNames.Completed,
        WorkOrderLifecycleStatus.Failed => WorkOrderLifecycleStatusNames.Failed,
        WorkOrderLifecycleStatus.Stopped => WorkOrderLifecycleStatusNames.Stopped,
        WorkOrderLifecycleStatus.Denied => WorkOrderLifecycleStatusNames.Denied,
        WorkOrderLifecycleStatus.Cancelled => WorkOrderLifecycleStatusNames.Cancelled,
        WorkOrderLifecycleStatus.TimedOut => WorkOrderLifecycleStatusNames.TimedOut,
        _ => string.Empty,
    };

    private static string ToWireName(WorkOrderApprovalStatus status) => status switch
    {
        WorkOrderApprovalStatus.NotRequired => WorkOrderApprovalStatusNames.NotRequired,
        WorkOrderApprovalStatus.Pending => WorkOrderApprovalStatusNames.Pending,
        WorkOrderApprovalStatus.Approved => WorkOrderApprovalStatusNames.Approved,
        WorkOrderApprovalStatus.Denied => WorkOrderApprovalStatusNames.Denied,
        _ => string.Empty,
    };

    private static long ToUnixTimeMilliseconds(Timestamp? timestamp) =>
        timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0;
}
