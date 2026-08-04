using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.Observability;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionCurrentStateProjector
    : MappedCurrentStateProjectionMaterializer<
        WorkflowExecutionMaterializationContext,
        WorkflowRunState,
        WorkflowExecutionCurrentStateDocument>
{
    private readonly WorkflowRunForkSeedReadModelMapper _forkSeedMapper = new();

    public WorkflowExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override WorkflowExecutionCurrentStateDocument? Map(
        MappedCurrentStateProjectionInput<WorkflowExecutionMaterializationContext, WorkflowRunState> input)
    {
        var context = input.Context;
        var publisherActorId = input.Envelope.Route?.PublisherActorId ?? string.Empty;
        // Refactor (issue1271/first-slice): Old pattern: reverse-relayed child WorkflowRunState roots
        // could overwrite the parent actor current-state document.
        // New principle: actor-scoped current-state readmodels only materialize facts committed by
        // the same authoritative actor as the projection scope; relayed child facts stay observable artifacts.
        if (!string.Equals(context.RootActorId, publisherActorId, StringComparison.Ordinal))
            return null;

        var stateEvent = input.StateEvent;
        WorkflowCompensationMetrics.ObserveCommittedPayload(stateEvent.EventData);

        var state = input.State;
        var seedSnapshot = _forkSeedMapper.ToProjectionSnapshot(state);

        // Refactor (iter97/cluster-591): Old/New
        //   Old: every current-state projector hand-rolled committed-state unpack, timestamp resolution, and upsert.
        //   New: core mapped helper owns that projection shell; this projector keeps only WorkflowRunState -> read model mapping.
        var document = new WorkflowExecutionCurrentStateDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId,
            WorkflowName = state.WorkflowName ?? string.Empty,
            Status = state.Status ?? string.Empty,
            ScopeId = state.ScopeId ?? string.Empty,
            RunOrigin = state.RunOrigin ?? string.Empty,
            ScheduleId = state.ScheduleId ?? string.Empty,
            ExpectedExecutionMode = state.ExpectedExecutionMode,
            Compiled = state.Compiled,
            CompilationError = state.CompilationError ?? string.Empty,
            Input = state.Input ?? string.Empty,
            FinalOutput = state.FinalOutput ?? string.Empty,
            FinalError = state.FinalError ?? string.Empty,
            SagaStatus = state.SagaStatus,
            DeadLetterFailedCompensationStepId = state.DeadLetterFailedCompensationStepId ?? string.Empty,
            DeadLetterRemainingUncompensated = state.DeadLetterRemainingUncompensated,
            DeadLetterError = state.DeadLetterError ?? string.Empty,
            ExecutionStateCount = state.ExecutionStates.Count,
            Success = ResolveSuccess(state.Status),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = input.ObservedAt,
            WorkflowYaml = seedSnapshot.WorkflowYaml,
            InlineWorkflowYamls = seedSnapshot.InlineWorkflowYamls.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedVariables = seedSnapshot.Variables.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedCompletedStepIds = seedSnapshot.CompletedStepIds.ToList(),
            ForkSeedLastFailedStepId = seedSnapshot.LastFailedStepId,
            ForkSeedIdempotencies = seedSnapshot.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => MapStepIdempotency(x.Value),
                StringComparer.Ordinal),
            InputFileRefs = seedSnapshot.InputFileRefs.Select(MapInputFileRef).ToList(),
            ConnectorApprovals = MapConnectorApprovals(state),
        };
        if (state.CapabilityAdmissionPlan is not null)
            document.CapabilityAdmissionPlan = state.CapabilityAdmissionPlan.Clone();

        // O2 (06-19-workflow-run-observatory): started_at is derived from the committed WorkflowRunState's
        // own start fact (StartedAtUtc), so the projector stays a pure committed-state -> readmodel mapper
        // (no prior-readmodel read). Absent for pre-existing runs -> run list falls back to updated_at.
        if (state.StartedAtUtc != null)
            document.StartedAtUtcValue = state.StartedAtUtc;

        return document;
    }

    private static IList<WorkflowExternalActionApprovalSnapshot> MapConnectorApprovals(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(ConnectorCallModuleState.Descriptor))
                continue;

            var connectorState = executionState.Unpack<ConnectorCallModuleState>();
            return connectorState.ApprovalsByActionId.Values
                .Where(static coordination => coordination.Snapshot?.Plan != null)
                .OrderBy(static coordination => coordination.Snapshot.Plan.ActionId, StringComparer.Ordinal)
                .Select(static coordination => coordination.Snapshot.Clone())
                .ToList();
        }

        return [];
    }

    private static WorkflowStepIdempotencyReadModel MapStepIdempotency(
        WorkflowStepIdempotencyState source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowStepIdempotencyReadModel
        {
            LogicalRunId = source.LogicalRunId ?? string.Empty,
            StepId = source.StepId ?? string.Empty,
            LogicalAttempt = source.LogicalAttempt,
            IdempotencyKey = source.IdempotencyKey ?? string.Empty,
        };
    }

    private static WorkflowExecutionInputFileRefReadModel MapInputFileRef(WorkflowFileRef source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionInputFileRefReadModel
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKindValue = (int)source.SourceKind,
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };
    }

    private static bool? ResolveSuccess(string? status)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "completed" => true,
            "failed" => false,
            _ => null,
        };
    }
}
