using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection.Projectors;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowRunIncrementalGraphMaterializer
{
    public const int MaximumCandidateMutationCount = ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount;

    private readonly IProjectionGraphOwnerIdentityResolver _ownerIdentityResolver;
    private readonly WorkflowRunGraphArtifactMaterializer _fullMaterializer = new();
    private readonly int _maximumCandidateMutationCount;

    public WorkflowRunIncrementalGraphMaterializer(
        IProjectionGraphOwnerIdentityResolver ownerIdentityResolver,
        int maximumCandidateMutationCount = MaximumCandidateMutationCount)
    {
        _ownerIdentityResolver = ownerIdentityResolver ??
                                 throw new ArgumentNullException(nameof(ownerIdentityResolver));
        if (maximumCandidateMutationCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidateMutationCount));
        _maximumCandidateMutationCount = maximumCandidateMutationCount;
    }

    public ProjectionGraphRouteFingerprint ResolveStoreRoute(
        string projectionKind,
        string reportId,
        ProjectionMaterializationRouteFingerprint route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new ProjectionGraphRouteFingerprint
        {
            ProjectionKind = projectionKind?.Trim() ?? string.Empty,
            LogicalScope = WorkflowExecutionGraphConstants.Scope,
            OwnerId = _ownerIdentityResolver
                .Resolve(typeof(WorkflowRunInsightReportDocument), reportId)
                .Value,
            PhysicalNamespace = route.PhysicalNamespace?.Trim() ?? string.Empty,
            RouteEpoch = route.RouteEpoch,
            ContractId = route.ContractId?.Trim() ?? string.Empty,
            ContractVersion = route.ContractVersion,
        };
    }

    public ProjectionGraphDelta BuildIncrementalDelta(
        WorkflowRunInsightReportDocument report,
        StateEvent stateEvent,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint route)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(stateEvent);
        var delta = CreateDelta(report, stateEvent, projectionKind, route, ProjectionGraphDeltaMode.Normal);
        var updatedAt = ResolveUpdatedAt(report);
        // The owner graph is per run, but the root actor node is shared across that actor's
        // runs and the OWNS edge is what makes a new run reachable from it. Neither is tied
        // to a step or topology event, so a run that starts on the incremental route (after
        // its scope already cut over) only ever sees them here; upserting them with the run
        // node is idempotent and keeps the incremental graph equivalent to the golden one.
        delta.UpsertNodes.Add(ToMutation(CreateRootActorNode(report, updatedAt)));
        delta.UpsertNodes.Add(ToMutation(CreateRunNode(report, updatedAt)));
        delta.UpsertEdges.Add(ToMutation(CreateOwnsEdge(report, updatedAt)));

        var payload = stateEvent.EventData;
        var stepId = ResolveChangedStepId(payload);
        if (stepId.Length > 0 &&
            WorkflowExecutionArtifactMaterializationSupport.TryGetIndexedStep(report, stepId, out var step))
        {
            var stepNode = CreateStepNode(report, step, updatedAt);
            delta.UpsertNodes.Add(ToMutation(stepNode));
            delta.UpsertEdges.Add(ToMutation(CreateContainsStepEdge(report, step, updatedAt)));

            if (payload?.Is(StepCompletedEvent.Descriptor) == true)
            {
                var completed = payload.Unpack<StepCompletedEvent>();
                AddNextMutation(delta, report, stepNode.NodeId, completed.NextStepId, completed.BranchKey, updatedAt);
            }
        }

        var topologyChild = ResolveTopologyChild(payload);
        if (topologyChild.Length > 0)
        {
            var childNode = CreateTopologyActorNode(report, topologyChild, updatedAt);
            delta.UpsertNodes.Add(ToMutation(childNode));
            delta.UpsertEdges.Add(ToMutation(CreateTopologyEdge(report, topologyChild, updatedAt)));
        }

        return delta;
    }

    /// <summary>
    /// The complete desired owner graph for the report, as a repair/cutover delta. Stale
    /// elements are not listed here: a repair/cutover delta is a full replacement and the
    /// versioned store deletes every owned element absent from it inside the apply
    /// transaction. That keeps the delta identity a function of the report alone, so a replay
    /// after a committed-but-unacknowledged apply is an exact duplicate rather than a conflict.
    /// </summary>
    public ProjectionGraphDelta BuildFullCandidateDelta(
        WorkflowRunInsightReportDocument report,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint route)
    {
        ArgumentNullException.ThrowIfNull(report);
        var stateEvent = new StateEvent
        {
            Version = report.StateVersion,
            EventId = report.LastEventId ?? string.Empty,
        };
        var delta = CreateDelta(
            report,
            stateEvent,
            projectionKind,
            route,
            ProjectionGraphDeltaMode.RepairOrCutover);
        var candidateReport = report.Clone();
        candidateReport.UpdatedAt = ResolveUpdatedAt(report);
        var materialization = _fullMaterializer.Materialize(candidateReport);
        delta.UpsertNodes.Add(materialization.Nodes.Select(ToMutation));
        delta.UpsertEdges.Add(materialization.Edges.Select(ToMutation));

        var liveEdgeIds = delta.UpsertEdges.Select(static edge => edge.EdgeId).ToHashSet(StringComparer.Ordinal);
        foreach (var step in report.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.NextStepId))
                continue;

            var sourceNodeId = WorkflowRunGraphArtifactMaterializer.BuildStepNodeId(
                report.RootActorId,
                report.CommandId,
                step.StepId);
            var edgeId = WorkflowRunGraphArtifactMaterializer.BuildNextEdgeId(sourceNodeId);
            if (liveEdgeIds.Contains(edgeId))
                continue;

            delta.UpsertPendingEdges.Add(ToMutation(CreateNextEdge(
                report,
                sourceNodeId,
                step.NextStepId,
                step.BranchKey,
                ResolveUpdatedAt(report))));
        }

        EnsureCandidateIsBounded(delta);
        return delta;
    }

    public string ComputeSnapshotFingerprint(ProjectionGraphOwnerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var delta = new ProjectionGraphDelta
        {
            Route = snapshot.Route?.Clone(),
            Source = snapshot.Source?.Clone(),
            Mode = ProjectionGraphDeltaMode.RepairOrCutover,
        };
        delta.UpsertNodes.Add(snapshot.Nodes);
        delta.UpsertEdges.Add(snapshot.Edges);
        delta.UpsertPendingEdges.Add(snapshot.PendingEdges);
        if (!ProjectionGraphDeltaContract.TryNormalize(delta, out _, out var fingerprint, out var detail))
            throw new InvalidOperationException($"Cannot fingerprint workflow graph snapshot: {detail}");

        return fingerprint;
    }

    public string ComputeExpectedCandidateFingerprint(
        WorkflowRunInsightReportDocument report,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint route)
    {
        var delta = BuildFullCandidateDelta(report, projectionKind, route);
        if (!ProjectionGraphDeltaContract.TryNormalize(delta, out _, out var fingerprint, out var detail))
            throw new InvalidOperationException($"Cannot fingerprint workflow graph candidate: {detail}");

        return fingerprint;
    }

    /// <summary>
    /// The report is keyed by the run actor and re-keys every step node under the latest
    /// command id, so a new command on the same actor changes the whole owner graph rather
    /// than one bounded neighbourhood. The typed command-observed event is the only fact
    /// that changes <c>LastCommandId</c>, so it is the only event that requires a bounded full
    /// replacement of the owner graph on the incremental route.
    /// </summary>
    public static bool RequiresOwnerGraphReplacement(StateEvent stateEvent)
    {
        ArgumentNullException.ThrowIfNull(stateEvent);
        return CommittedStateRepublish.IsRepublishEventId(stateEvent.EventId) ||
               stateEvent.EventData?.Is(WorkflowCommandObservedEvent.Descriptor) == true;
    }

    public static bool IsIncrementalRoute(ProjectionMaterializationRouteFingerprint? route) =>
        route is
        {
            RouteEpoch: > 0,
            ContractVersion: RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
        } &&
        string.Equals(
            route.ContractId,
            RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(route.PhysicalNamespace) &&
        string.Equals(
            route.PhysicalNamespace,
            route.PhysicalNamespace.Trim(),
            StringComparison.Ordinal) &&
        !string.Equals(
            route.PhysicalNamespace,
            WorkflowExecutionGraphConstants.Scope,
            StringComparison.Ordinal);

    private ProjectionGraphDelta CreateDelta(
        WorkflowRunInsightReportDocument report,
        StateEvent stateEvent,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint route,
        ProjectionGraphDeltaMode mode) =>
        new()
        {
            Route = ResolveStoreRoute(projectionKind, report.Id, route),
            Source = new ProjectionGraphSourceCoordinate
            {
                ActorId = report.RootActorId?.Trim() ?? string.Empty,
                StateVersion = stateEvent.Version,
                EventId = stateEvent.EventId?.Trim() ?? string.Empty,
            },
            Mode = mode,
        };

    private static void AddNextMutation(
        ProjectionGraphDelta delta,
        WorkflowRunInsightReportDocument report,
        string sourceNodeId,
        string? nextStepId,
        string? branchKey,
        DateTimeOffset updatedAt)
    {
        var edgeId = WorkflowRunGraphArtifactMaterializer.BuildNextEdgeId(sourceNodeId);
        if (string.IsNullOrWhiteSpace(nextStepId))
        {
            delta.DeleteEdgeIds.Add(edgeId);
            return;
        }

        delta.UpsertPendingEdges.Add(ToMutation(CreateNextEdge(
            report,
            sourceNodeId,
            nextStepId,
            branchKey,
            updatedAt)));
    }

    private static ProjectionGraphNode CreateRootActorNode(
        WorkflowRunInsightReportDocument report,
        DateTimeOffset updatedAt)
    {
        var rootActorId = NormalizeToken(report.RootActorId);
        return WorkflowRunGraphArtifactMaterializer.CreateActorNode(
            rootActorId,
            rootActorId,
            report.WorkflowName,
            updatedAt);
    }

    private static ProjectionGraphEdge CreateOwnsEdge(
        WorkflowRunInsightReportDocument report,
        DateTimeOffset updatedAt) =>
        WorkflowRunGraphArtifactMaterializer.CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeOwns,
            NormalizeToken(report.RootActorId),
            WorkflowRunGraphArtifactMaterializer.BuildRunNodeId(report.RootActorId, report.CommandId),
            new Dictionary<string, string>(StringComparer.Ordinal),
            updatedAt);

    private static ProjectionGraphNode CreateRunNode(
        WorkflowRunInsightReportDocument report,
        DateTimeOffset updatedAt) =>
        new()
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = WorkflowRunGraphArtifactMaterializer.BuildRunNodeId(report.RootActorId, report.CommandId),
            NodeType = WorkflowExecutionGraphConstants.RunNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowExecutionGraphConstants.RootActorIdPropertyKey] = NormalizeToken(report.RootActorId),
                ["workflowName"] = report.WorkflowName ?? string.Empty,
                ["commandId"] = NormalizeToken(report.CommandId),
                ["input"] = report.Input ?? string.Empty,
                [WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey] = report.StateVersion.ToString(),
            },
            UpdatedAt = updatedAt,
        };

    private static ProjectionGraphNode CreateStepNode(
        WorkflowRunInsightReportDocument report,
        WorkflowExecutionStepTrace step,
        DateTimeOffset updatedAt) =>
        new()
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = WorkflowRunGraphArtifactMaterializer.BuildStepNodeId(
                report.RootActorId,
                report.CommandId,
                step.StepId),
            NodeType = WorkflowExecutionGraphConstants.StepNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = NormalizeToken(report.RootActorId),
                ["commandId"] = NormalizeToken(report.CommandId),
                ["stepId"] = NormalizeToken(step.StepId),
                ["stepType"] = step.StepType ?? string.Empty,
                ["targetRole"] = step.TargetRole ?? string.Empty,
                ["workerId"] = step.WorkerId ?? string.Empty,
                ["success"] = step.Success?.ToString() ?? string.Empty,
                ["displayName"] = string.IsNullOrWhiteSpace(step.DisplayName) ? step.StepId : step.DisplayName,
            },
            UpdatedAt = updatedAt,
        };

    private static ProjectionGraphEdge CreateContainsStepEdge(
        WorkflowRunInsightReportDocument report,
        WorkflowExecutionStepTrace step,
        DateTimeOffset updatedAt) =>
        WorkflowRunGraphArtifactMaterializer.CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeContainsStep,
            WorkflowRunGraphArtifactMaterializer.BuildRunNodeId(report.RootActorId, report.CommandId),
            WorkflowRunGraphArtifactMaterializer.BuildStepNodeId(report.RootActorId, report.CommandId, step.StepId),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stepId"] = NormalizeToken(step.StepId),
                ["stepType"] = step.StepType ?? string.Empty,
            },
            updatedAt);

    private static ProjectionGraphNode CreateTopologyActorNode(
        WorkflowRunInsightReportDocument report,
        string childActorId,
        DateTimeOffset updatedAt)
    {
        var nodeId = WorkflowRunGraphArtifactMaterializer.BuildTopologyActorNodeId(
            report.RootActorId,
            report.CommandId,
            childActorId);
        return WorkflowRunGraphArtifactMaterializer.CreateActorNode(
            nodeId,
            childActorId,
            report.WorkflowName,
            updatedAt);
    }

    private static ProjectionGraphEdge CreateTopologyEdge(
        WorkflowRunInsightReportDocument report,
        string childActorId,
        DateTimeOffset updatedAt) =>
        WorkflowRunGraphArtifactMaterializer.CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeChildOf,
            WorkflowRunGraphArtifactMaterializer.BuildTopologyActorNodeId(
                report.RootActorId,
                report.CommandId,
                report.RootActorId),
            WorkflowRunGraphArtifactMaterializer.BuildTopologyActorNodeId(
                report.RootActorId,
                report.CommandId,
                childActorId),
            new Dictionary<string, string>(StringComparer.Ordinal),
            updatedAt);

    private static ProjectionGraphEdge CreateNextEdge(
        WorkflowRunInsightReportDocument report,
        string sourceNodeId,
        string nextStepId,
        string? branchKey,
        DateTimeOffset updatedAt)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(branchKey))
            properties["branchKey"] = branchKey;
        return WorkflowRunGraphArtifactMaterializer.CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeNext,
            sourceNodeId,
            WorkflowRunGraphArtifactMaterializer.BuildStepNodeId(
                report.RootActorId,
                report.CommandId,
                nextStepId),
            properties,
            updatedAt);
    }

    private static string ResolveChangedStepId(Google.Protobuf.WellKnownTypes.Any? payload)
    {
        if (payload?.Is(StepRequestEvent.Descriptor) == true)
            return payload.Unpack<StepRequestEvent>().StepId?.Trim() ?? string.Empty;
        if (payload?.Is(StepCompletedEvent.Descriptor) == true)
            return payload.Unpack<StepCompletedEvent>().StepId?.Trim() ?? string.Empty;
        if (payload?.Is(WorkflowSuspendedEvent.Descriptor) == true)
            return payload.Unpack<WorkflowSuspendedEvent>().StepId?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static string ResolveTopologyChild(Google.Protobuf.WellKnownTypes.Any? payload)
    {
        if (payload?.Is(WorkflowRoleActorLinkedEvent.Descriptor) == true)
            return payload.Unpack<WorkflowRoleActorLinkedEvent>().ChildActorId?.Trim() ?? string.Empty;
        if (payload?.Is(SubWorkflowBindingUpsertedEvent.Descriptor) == true)
            return payload.Unpack<SubWorkflowBindingUpsertedEvent>().ChildActorId?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private void EnsureCandidateIsBounded(ProjectionGraphDelta delta)
    {
        var mutationCount = ProjectionGraphDeltaContract.CountMutations(delta);
        if (mutationCount > _maximumCandidateMutationCount)
            throw new ProjectionGraphCandidateOverBoundException(mutationCount, _maximumCandidateMutationCount);
    }

    private static ProjectionGraphNodeMutation ToMutation(ProjectionGraphNode node)
    {
        var mutation = new ProjectionGraphNodeMutation
        {
            NodeId = node.NodeId,
            NodeType = node.NodeType,
            UpdatedAtEpochMs = node.UpdatedAt == default ? 0 : node.UpdatedAt.ToUnixTimeMilliseconds(),
        };
        mutation.Properties.Add(node.Properties);
        return mutation;
    }

    private static ProjectionGraphEdgeMutation ToMutation(ProjectionGraphEdge edge)
    {
        var mutation = new ProjectionGraphEdgeMutation
        {
            EdgeId = edge.EdgeId,
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            EdgeType = edge.EdgeType,
            UpdatedAtEpochMs = edge.UpdatedAt == default ? 0 : edge.UpdatedAt.ToUnixTimeMilliseconds(),
        };
        mutation.Properties.Add(edge.Properties);
        return mutation;
    }

    private static DateTimeOffset ResolveUpdatedAt(WorkflowRunInsightReportDocument report) =>
        report.UpdatedAt == default ? DateTimeOffset.UnixEpoch : report.UpdatedAt;

    private static string NormalizeToken(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? "unknown" : normalized;
    }
}
