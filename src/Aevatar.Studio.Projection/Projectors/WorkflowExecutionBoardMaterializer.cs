using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class WorkflowExecutionBoardMaterializer
    : IProjectionArtifactMaterializer<StudioWorkflowBoardMaterializationContext>
{
    private delegate bool BoardPayloadHandler(
        WorkflowExecutionBoardDocument document,
        Any payload,
        DateTimeOffset observedAt);

    private static readonly IReadOnlyDictionary<string, BoardPayloadHandler> PayloadHandlers =
        new Dictionary<string, BoardPayloadHandler>(StringComparer.Ordinal)
        {
            [BuildTypeUrl(WorkflowRunExecutionStartedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyExecutionStarted(document, payload.Unpack<WorkflowRunExecutionStartedEvent>(), observedAt),
            [BuildTypeUrl(StepRequestEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyStepRequest(document, payload.Unpack<StepRequestEvent>(), observedAt),
            [BuildTypeUrl(WorkflowExecutionStateUpsertedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWorkflowExecutionStateUpserted(document, payload.Unpack<WorkflowExecutionStateUpsertedEvent>(), observedAt),
            [BuildTypeUrl(WorkflowSuspendedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWorkflowSuspended(document, payload.Unpack<WorkflowSuspendedEvent>(), observedAt),
            [BuildTypeUrl(WaitingForSignalEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWaitingForSignal(document, payload.Unpack<WaitingForSignalEvent>(), observedAt),
            [BuildTypeUrl(StepCompletedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyStepCompleted(document, payload.Unpack<StepCompletedEvent>(), observedAt),
            [BuildTypeUrl(WorkflowCompletedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWorkflowCompleted(document, payload.Unpack<WorkflowCompletedEvent>(), observedAt),
            [BuildTypeUrl(WorkflowStoppedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWorkflowStopped(document, payload.Unpack<WorkflowStoppedEvent>(), observedAt),
            [BuildTypeUrl(WorkflowRunStoppedEvent.Descriptor)] = static (document, payload, observedAt) =>
                ApplyWorkflowRunStopped(document, payload.Unpack<WorkflowRunStoppedEvent>(), observedAt),
        };

    private readonly IProjectionDocumentReader<WorkflowExecutionBoardDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<WorkflowExecutionBoardDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowExecutionBoardMaterializer(
        IProjectionDocumentReader<WorkflowExecutionBoardDocument, string> documentReader,
        IProjectionWriteDispatcher<WorkflowExecutionBoardDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioWorkflowBoardMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!TryUnpackRootStateEnvelope(
                envelope,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (!string.Equals(context.RootActorId, publisherActorId, StringComparison.Ordinal))
            return;

        var existing = await _documentReader.GetAsync(context.RootActorId, ct);
        if (existing != null && ShouldSkip(existing, stateEvent))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = existing?.Clone() ?? CreateDocument(context, state, observedAt);

        ApplyBase(document, context, state, stateEvent, observedAt);
        var changedByDefinitionStepCount = ApplyDefinitionStepCount(document, state);
        var changedByPayload = ApplyPayload(document, stateEvent.EventData, observedAt);
        var changedByStateRoot = ApplyPendingExecutionStates(document, state, observedAt);
        var changedByWorkflowDefinition = ApplyUnexecutedDownstreamSteps(document, state, observedAt);
        if (changedByDefinitionStepCount || changedByStateRoot || changedByWorkflowDefinition || (!changedByPayload && existing != null))
            RefreshSummary(document);

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static WorkflowExecutionBoardDocument CreateDocument(
        StudioWorkflowBoardMaterializationContext context,
        WorkflowRunState state,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId,
            WorkflowName = state.WorkflowName ?? string.Empty,
            ScopeId = state.ScopeId ?? string.Empty,
            CompletionStatus = ResolveCompletionStatus(state.Status),
            UpdatedAt = observedAt,
        };

    private static void ApplyBase(
        WorkflowExecutionBoardDocument document,
        StudioWorkflowBoardMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        document.Id = context.RootActorId;
        document.RootActorId = context.RootActorId;
        document.CommandId = state.LastCommandId ?? string.Empty;
        document.DefinitionActorId = state.DefinitionActorId ?? string.Empty;
        document.RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId;
        document.WorkflowName = state.WorkflowName ?? string.Empty;
        document.ScopeId = state.ScopeId ?? string.Empty;
        document.CompletionStatus = ResolveCompletionStatus(state.Status);
        document.StateVersion = stateEvent.Version;
        document.LastEventId = stateEvent.EventId ?? string.Empty;
        document.UpdatedAt = observedAt;
    }

    private static bool ApplyPayload(
        WorkflowExecutionBoardDocument document,
        Any payload,
        DateTimeOffset observedAt)
    {
        if (!PayloadHandlers.TryGetValue(payload.TypeUrl ?? string.Empty, out var handler))
        {
            RefreshSummary(document);
            return false;
        }

        var changed = handler(document, payload, observedAt);
        RefreshSummary(document);
        return changed;
    }

    private static bool ApplyExecutionStarted(
        WorkflowExecutionBoardDocument document,
        WorkflowRunExecutionStartedEvent evt,
        DateTimeOffset observedAt)
    {
        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? document.WorkflowName : evt.WorkflowName;
        document.DefinitionActorId = string.IsNullOrWhiteSpace(evt.DefinitionActorId)
            ? document.DefinitionActorId
            : evt.DefinitionActorId;
        document.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? document.ScopeId : evt.ScopeId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.Running;
        document.CurrentNodeId = string.Empty;
        document.LastNodeUpdatedAt ??= observedAt;
        return true;
    }

    private static bool ApplyStepRequest(
        WorkflowExecutionBoardDocument document,
        StepRequestEvent evt,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(evt.StepId);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        node.NodeId = stepId;
        node.StepType = evt.StepType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = stepId;
        if (node.Order == 0)
            node.Order = ResolveNextOrder(document.NodeEntries);
        node.Status = WorkflowExecutionBoardNodeStatus.Running;
        node.RequestedAt = observedAt;
        node.CompletedAt = null;
        node.UpdatedAt = observedAt;
        node.DurationMs = 0;
        node.NextNodeId = string.Empty;
        node.BranchKey = string.Empty;

        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.Running;
        document.CurrentNodeId = stepId;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWorkflowExecutionStateUpserted(
        WorkflowExecutionBoardDocument document,
        WorkflowExecutionStateUpsertedEvent evt,
        DateTimeOffset observedAt)
    {
        var scopeKey = evt.ScopeKey?.Trim() ?? string.Empty;
        if (evt.State == null || string.IsNullOrWhiteSpace(scopeKey))
            return false;

        if (evt.State.Is(WaitSignalModuleState.Descriptor))
        {
            return ApplyPendingSignalState(
                document,
                evt.State.Unpack<WaitSignalModuleState>(),
                "wait_signal",
                observedAt);
        }

        if (evt.State.Is(HumanInputModuleState.Descriptor))
        {
            return ApplyPendingHumanInputState(
                document,
                evt.State.Unpack<HumanInputModuleState>(),
                observedAt);
        }

        if (evt.State.Is(HumanApprovalModuleState.Descriptor))
        {
            return ApplyPendingHumanApprovalState(
                document,
                evt.State.Unpack<HumanApprovalModuleState>(),
                observedAt);
        }

        return false;
    }

    private static bool ApplyPendingSignalState(
        WorkflowExecutionBoardDocument document,
        WaitSignalModuleState state,
        string stepType,
        DateTimeOffset observedAt)
    {
        var changed = false;
        foreach (var pending in state.Pending.Values)
        {
            changed |= ApplyWaitingNode(
                document,
                pending.StepId,
                pending.RunId,
                stepType,
                observedAt);
        }

        return changed;
    }

    private static bool ApplyPendingExecutionStates(
        WorkflowExecutionBoardDocument document,
        WorkflowRunState state,
        DateTimeOffset observedAt)
    {
        var changed = false;
        foreach (var executionState in state.ExecutionStates.Values)
        {
            if (executionState.Is(WaitSignalModuleState.Descriptor))
            {
                changed |= ApplyPendingSignalState(
                    document,
                    executionState.Unpack<WaitSignalModuleState>(),
                    "wait_signal",
                    observedAt);
                continue;
            }

            if (executionState.Is(HumanInputModuleState.Descriptor))
            {
                changed |= ApplyPendingHumanInputState(
                    document,
                    executionState.Unpack<HumanInputModuleState>(),
                    observedAt);
                continue;
            }

            if (executionState.Is(HumanApprovalModuleState.Descriptor))
            {
                changed |= ApplyPendingHumanApprovalState(
                    document,
                    executionState.Unpack<HumanApprovalModuleState>(),
                    observedAt);
            }
        }

        return changed;
    }

    private static bool ApplyUnexecutedDownstreamSteps(
        WorkflowExecutionBoardDocument document,
        WorkflowRunState state,
        DateTimeOffset observedAt)
    {
        if (document.CompletionStatus != WorkflowExecutionBoardCompletionStatus.WaitingForSignal ||
            string.IsNullOrWhiteSpace(state.WorkflowYaml) ||
            !TryParseWorkflowDefinition(state.WorkflowYaml, out var workflow))
        {
            return false;
        }

        var waitingStepIds = document.NodeEntries
            .Where(static node => node.Status == WorkflowExecutionBoardNodeStatus.Waiting)
            .Select(static node => NormalizeStepId(node.NodeId))
            .Where(static stepId => !string.IsNullOrWhiteSpace(stepId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (waitingStepIds.Length == 0)
            return false;

        var orderByStepId = workflow.Steps
            .Select(static (step, index) => (StepId: NormalizeStepId(step.Id), Order: index + 1))
            .Where(static item => !string.IsNullOrWhiteSpace(item.StepId))
            .GroupBy(static item => item.StepId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Order, StringComparer.Ordinal);
        var changed = false;

        foreach (var waitingStepId in waitingStepIds)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { waitingStepId };
            for (var next = workflow.GetNextStep(waitingStepId);
                 next != null && visited.Add(NormalizeStepId(next.Id));
                 next = workflow.GetNextStep(next.Id))
            {
                var stepId = NormalizeStepId(next.Id);
                if (string.IsNullOrWhiteSpace(stepId))
                    break;

                changed |= ApplyPendingPlannedNode(
                    document,
                    next,
                    orderByStepId.GetValueOrDefault(stepId),
                    observedAt);
            }
        }

        return changed;
    }

    private static bool TryParseWorkflowDefinition(
        string workflowYaml,
        out WorkflowDefinition workflow)
    {
        try
        {
            workflow = new WorkflowParser().Parse(workflowYaml);
            return true;
        }
        catch (InvalidOperationException)
        {
            workflow = null!;
            return false;
        }
        catch (ArgumentException)
        {
            workflow = null!;
            return false;
        }
    }

    private static bool ApplyDefinitionStepCount(
        WorkflowExecutionBoardDocument document,
        WorkflowRunState state)
    {
        if (string.IsNullOrWhiteSpace(state.WorkflowYaml) ||
            !TryParseWorkflowDefinition(state.WorkflowYaml, out var workflow))
        {
            return false;
        }

        var stepCount = CountDefinitionSteps(workflow.Steps);
        if (document.Summary.HasDefinitionStepCount &&
            document.Summary.DefinitionStepCount == stepCount)
        {
            return false;
        }

        document.Summary.DefinitionStepCount = stepCount;
        return true;
    }

    private static int CountDefinitionSteps(IReadOnlyList<StepDefinition>? steps)
    {
        if (steps is null)
            return 0;

        var count = 0;
        foreach (var step in steps)
        {
            count++;
            if (step.Children is { Count: > 0 })
                count += CountDefinitionSteps(step.Children);
        }

        return count;
    }

    private static bool ApplyPendingPlannedNode(
        WorkflowExecutionBoardDocument document,
        StepDefinition step,
        int plannedOrder,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(step.Id);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        var changed = false;

        if (node.NodeId != stepId)
        {
            node.NodeId = stepId;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            node.Name = stepId;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(node.StepType) && !string.IsNullOrWhiteSpace(step.Type))
        {
            node.StepType = step.Type;
            changed = true;
        }

        if (node.Order == 0 && plannedOrder > 0)
        {
            node.Order = plannedOrder;
            changed = true;
        }

        if (node.Status is WorkflowExecutionBoardNodeStatus.Unknown or WorkflowExecutionBoardNodeStatus.Pending)
        {
            if (node.Status != WorkflowExecutionBoardNodeStatus.Pending)
            {
                node.Status = WorkflowExecutionBoardNodeStatus.Pending;
                changed = true;
            }

            if (!node.UpdatedAt.HasValue)
            {
                node.UpdatedAt = observedAt;
                changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyPendingHumanInputState(
        WorkflowExecutionBoardDocument document,
        HumanInputModuleState state,
        DateTimeOffset observedAt)
    {
        var changed = false;
        foreach (var pending in state.Pending.Values)
        {
            changed |= ApplyWaitingNode(
                document,
                pending.StepId,
                pending.RunId,
                "human_input",
                observedAt);
        }

        return changed;
    }

    private static bool ApplyPendingHumanApprovalState(
        WorkflowExecutionBoardDocument document,
        HumanApprovalModuleState state,
        DateTimeOffset observedAt)
    {
        var changed = false;
        foreach (var pending in state.Pending.Values)
        {
            changed |= ApplyWaitingNode(
                document,
                pending.StepId,
                pending.RunId,
                "human_approval",
                observedAt);
        }

        return changed;
    }

    private static bool ApplyWaitingNode(
        WorkflowExecutionBoardDocument document,
        string? rawStepId,
        string? rawRunId,
        string stepType,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(rawStepId);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        node.NodeId = stepId;
        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = stepId;
        if (node.Order == 0)
            node.Order = ResolveNextOrder(document.NodeEntries);
        if (string.IsNullOrWhiteSpace(node.StepType))
            node.StepType = stepType;
        node.Status = WorkflowExecutionBoardNodeStatus.Waiting;
        node.UpdatedAt = observedAt;

        document.RunId = string.IsNullOrWhiteSpace(rawRunId) ? document.RunId : rawRunId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.WaitingForSignal;
        document.CurrentNodeId = stepId;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWorkflowSuspended(
        WorkflowExecutionBoardDocument document,
        WorkflowSuspendedEvent evt,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(evt.StepId);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        node.NodeId = stepId;
        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = stepId;
        if (node.Order == 0)
            node.Order = ResolveNextOrder(document.NodeEntries);
        node.Status = WorkflowExecutionBoardNodeStatus.Waiting;
        node.UpdatedAt = observedAt;

        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.WaitingForSignal;
        document.CurrentNodeId = stepId;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWaitingForSignal(
        WorkflowExecutionBoardDocument document,
        WaitingForSignalEvent evt,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(evt.StepId);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        node.NodeId = stepId;
        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = stepId;
        if (node.Order == 0)
            node.Order = ResolveNextOrder(document.NodeEntries);
        node.Status = WorkflowExecutionBoardNodeStatus.Waiting;
        node.UpdatedAt = observedAt;

        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.WaitingForSignal;
        document.CurrentNodeId = stepId;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyStepCompleted(
        WorkflowExecutionBoardDocument document,
        StepCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        var stepId = NormalizeStepId(evt.StepId);
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        var node = GetOrCreateNode(document.NodeEntries, stepId);
        node.NodeId = stepId;
        if (string.IsNullOrWhiteSpace(node.Name))
            node.Name = stepId;
        if (node.Order == 0)
            node.Order = ResolveNextOrder(document.NodeEntries);
        node.Status = evt.Success
            ? WorkflowExecutionBoardNodeStatus.Completed
            : WorkflowExecutionBoardNodeStatus.Failed;
        node.CompletedAt = observedAt;
        node.UpdatedAt = observedAt;
        node.DurationMs = ResolveDurationMs(node.RequestedAt, observedAt);
        node.NextNodeId = evt.NextStepId ?? string.Empty;
        node.BranchKey = evt.BranchKey ?? string.Empty;

        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.CompletionStatus = evt.Success
            ? WorkflowExecutionBoardCompletionStatus.Running
            : WorkflowExecutionBoardCompletionStatus.Failed;
        document.CurrentNodeId = string.Empty;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWorkflowCompleted(
        WorkflowExecutionBoardDocument document,
        WorkflowCompletedEvent evt,
        DateTimeOffset observedAt)
    {
        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? document.WorkflowName : evt.WorkflowName;
        document.CompletionStatus = evt.Success
            ? WorkflowExecutionBoardCompletionStatus.Completed
            : WorkflowExecutionBoardCompletionStatus.Failed;
        document.CurrentNodeId = string.Empty;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWorkflowStopped(
        WorkflowExecutionBoardDocument document,
        WorkflowStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? document.WorkflowName : evt.WorkflowName;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.Stopped;
        document.CurrentNodeId = string.Empty;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static bool ApplyWorkflowRunStopped(
        WorkflowExecutionBoardDocument document,
        WorkflowRunStoppedEvent evt,
        DateTimeOffset observedAt)
    {
        document.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? document.RunId : evt.RunId;
        document.CompletionStatus = WorkflowExecutionBoardCompletionStatus.Stopped;
        document.CurrentNodeId = string.Empty;
        document.LastNodeUpdatedAt = observedAt;
        return true;
    }

    private static WorkflowExecutionBoardNodeReadModel GetOrCreateNode(
        IList<WorkflowExecutionBoardNodeReadModel> nodes,
        string stepId)
    {
        var existing = nodes.FirstOrDefault(node =>
            string.Equals(node.NodeId ?? string.Empty, stepId, StringComparison.Ordinal));
        if (existing != null)
            return existing;

        existing = new WorkflowExecutionBoardNodeReadModel
        {
            NodeId = stepId,
            Name = stepId,
            Order = ResolveNextOrder(nodes),
        };
        nodes.Add(existing);
        return existing;
    }

    private static void RefreshSummary(WorkflowExecutionBoardDocument document)
    {
        var summary = document.Summary;
        summary.CompletedSteps = document.NodeEntries.Count(static node =>
            node.Status == WorkflowExecutionBoardNodeStatus.Completed);
        summary.RunningNodes = document.NodeEntries.Count(static node =>
            node.Status == WorkflowExecutionBoardNodeStatus.Running);
        summary.WaitingOrPendingNodes = document.NodeEntries.Count(static node =>
            node.Status is WorkflowExecutionBoardNodeStatus.Waiting or WorkflowExecutionBoardNodeStatus.Pending);
        summary.FailedNodes = document.NodeEntries.Count(static node =>
            node.Status == WorkflowExecutionBoardNodeStatus.Failed);
    }

    private static WorkflowExecutionBoardCompletionStatus ResolveCompletionStatus(string? status)
    {
        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "completed" => WorkflowExecutionBoardCompletionStatus.Completed,
            "failed" => WorkflowExecutionBoardCompletionStatus.Failed,
            "stopped" => WorkflowExecutionBoardCompletionStatus.Stopped,
            "waiting" or "waiting_for_signal" or "suspended" => WorkflowExecutionBoardCompletionStatus.WaitingForSignal,
            "running" => WorkflowExecutionBoardCompletionStatus.Running,
            _ => WorkflowExecutionBoardCompletionStatus.Unknown,
        };
    }

    private static bool TryUnpackRootStateEnvelope(
        EventEnvelope envelope,
        out StateEvent? stateEvent,
        out WorkflowRunState? state)
    {
        if (CommittedStateEventEnvelope.TryUnpackState<WorkflowRunState>(
                envelope,
                out _,
                out stateEvent,
                out state) &&
            stateEvent != null &&
            state != null)
        {
            return true;
        }

        stateEvent = null;
        state = null;
        return false;
    }

    private static bool ShouldSkip(IProjectionReadModel existing, StateEvent stateEvent)
    {
        if (existing.StateVersion > stateEvent.Version)
            return true;

        return existing.StateVersion == stateEvent.Version &&
               string.Equals(existing.LastEventId, stateEvent.EventId ?? string.Empty, StringComparison.Ordinal);
    }

    private static int ResolveNextOrder(IEnumerable<WorkflowExecutionBoardNodeReadModel> nodes)
    {
        var max = nodes.Select(static node => node.Order).DefaultIfEmpty(0).Max();
        return max + 1;
    }

    private static long ResolveDurationMs(DateTimeOffset? requestedAt, DateTimeOffset completedAt) =>
        requestedAt.HasValue
            ? Math.Max(0, (long)(completedAt - requestedAt.Value).TotalMilliseconds)
            : 0;

    private static string NormalizeStepId(string? stepId) =>
        stepId?.Trim() ?? string.Empty;

    private static string BuildTypeUrl(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return "type.googleapis.com/" + descriptor.FullName;
    }
}
