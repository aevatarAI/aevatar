using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
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
            WorkflowId = state.WorkflowId ?? string.Empty,
            WorkflowName = state.WorkflowName ?? string.Empty,
            Status = ResolveCurrentStateStatus(state),
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
            CompletedAtUtcValue = state.CompletedAtUtc?.Clone(),
            DurationMs = state.DurationMs,
            ActivityInitiator = MapInitiator(state.Initiator),
            InputSummary = WorkflowAuditTextSanitizer.SanitizeForDisplay(state.Input, 240),
            ActivityCurrentStep = ResolveCurrentStep(state),
            ActivityFirstFailure = ResolveFirstFailure(state),
            ActivityWaiting = ResolveWaiting(state),
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

    private static WorkflowRunActivityInitiatorReadModel MapInitiator(WorkflowRunInitiatorState? initiator)
    {
        if (initiator == null)
        {
            return new WorkflowRunActivityInitiatorReadModel
            {
                Availability = "unavailable",
                DisplayValue = "Unknown",
            };
        }

        return new WorkflowRunActivityInitiatorReadModel
        {
            Platform = WorkflowAuditTextSanitizer.Sanitize(initiator.Platform),
            Tenant = WorkflowAuditTextSanitizer.Sanitize(initiator.Tenant),
            ExternalUserId = WorkflowAuditTextSanitizer.Sanitize(initiator.ExternalUserId),
            Scope = WorkflowAuditTextSanitizer.Sanitize(initiator.Scope),
            BindingId = WorkflowAuditTextSanitizer.Sanitize(initiator.BindingId),
            DisplayValue = string.IsNullOrWhiteSpace(initiator.DisplayValue)
                ? "Unknown"
                : WorkflowAuditTextSanitizer.Sanitize(initiator.DisplayValue),
            Availability = string.IsNullOrWhiteSpace(initiator.Availability)
                ? "unavailable"
                : WorkflowAuditTextSanitizer.Sanitize(initiator.Availability),
        };
    }

    private static WorkflowRunActivityStepReadModel ResolveCurrentStep(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(WorkflowExecutionKernelState.Descriptor))
                continue;

            var kernelState = executionState.Unpack<WorkflowExecutionKernelState>();
            if (kernelState == null || string.IsNullOrWhiteSpace(kernelState.CurrentStepId))
                break;

            return new WorkflowRunActivityStepReadModel
            {
                StepId = WorkflowAuditTextSanitizer.Sanitize(kernelState.CurrentStepId),
                InputSummary = WorkflowAuditTextSanitizer.SanitizeForDisplay(kernelState.CurrentStepInput, 160),
                Availability = "available",
            };
        }

        return new WorkflowRunActivityStepReadModel
        {
            Availability = "unavailable",
        };
    }

    private static WorkflowRunActivityFailureReadModel ResolveFirstFailure(WorkflowRunState state)
    {
        var message = !string.IsNullOrWhiteSpace(state.FinalError)
            ? state.FinalError
            : state.DeadLetterError;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new WorkflowRunActivityFailureReadModel
            {
                Availability = "unavailable",
            };
        }

        return new WorkflowRunActivityFailureReadModel
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(ResolveFailureStepId(state)),
            Message = WorkflowAuditTextSanitizer.SanitizeForDisplay(message, 240),
            Availability = "available",
        };
    }

    private static string ResolveFailureStepId(WorkflowRunState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CompensationOriginFailedStepId))
            return state.CompensationOriginFailedStepId;
        if (!string.IsNullOrWhiteSpace(state.DeadLetterFailedCompensationStepId))
            return state.DeadLetterFailedCompensationStepId;
        return string.Empty;
    }

    private static WorkflowRunActivityWaitingReadModel ResolveWaiting(WorkflowRunState state)
    {
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (executionState.Is(ToolCallModuleState.Descriptor))
            {
                var toolCallState = executionState.Unpack<ToolCallModuleState>();
                var pending = toolCallState?.PendingApprovals.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "tool_approval", $"Approve {pending.ToolName}");
            }

            if (executionState.Is(HumanApprovalModuleState.Descriptor))
            {
                var approvalState = executionState.Unpack<HumanApprovalModuleState>();
                var pending = approvalState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "human_approval", pending.Input);
            }

            if (executionState.Is(HumanInputModuleState.Descriptor))
            {
                var humanInputState = executionState.Unpack<HumanInputModuleState>();
                var pending = humanInputState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "human_input", pending.Input);
            }

            if (executionState.Is(WaitSignalModuleState.Descriptor))
            {
                var signalState = executionState.Unpack<WaitSignalModuleState>();
                var pending = signalState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "signal", pending.SignalName);
            }

            if (executionState.Is(SecureInputModuleState.Descriptor))
            {
                var secureInputState = executionState.Unpack<SecureInputModuleState>();
                var pending = secureInputState?.Pending.Values
                    .OrderBy(static item => item.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pending != null)
                    return Waiting(pending.StepId, "secure_input", pending.Input);
            }

            if (executionState.Is(DelayModuleState.Descriptor))
            {
                var delayState = executionState.Unpack<DelayModuleState>();
                if (delayState?.Pending.Count > 0)
                {
                    var pending = delayState.Pending
                        .OrderBy(static item => item.Key, StringComparer.Ordinal)
                        .First();
                    return Waiting(pending.Value.CallbackId, "delay", pending.Value.Input);
                }
            }
        }

        return new WorkflowRunActivityWaitingReadModel
        {
            Availability = "unavailable",
        };
    }

    private static WorkflowRunActivityWaitingReadModel Waiting(string? stepId, string waitingKind, string? prompt) =>
        new()
        {
            StepId = WorkflowAuditTextSanitizer.Sanitize(stepId),
            WaitingKind = WorkflowAuditTextSanitizer.Sanitize(waitingKind),
            Prompt = WorkflowAuditTextSanitizer.SanitizeForDisplay(prompt, 160),
            Availability = "available",
        };

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

    private static string ResolveCurrentStateStatus(WorkflowRunState state)
    {
        var status = state.Status ?? string.Empty;
        if (status is "completed" or "failed" or "stopped")
            return status;

        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (!executionState.Is(ToolCallModuleState.Descriptor))
                continue;

            if (executionState.Unpack<ToolCallModuleState>().PendingApprovals.Count > 0)
                return "awaiting_tool_approval";
        }

        return status;
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
