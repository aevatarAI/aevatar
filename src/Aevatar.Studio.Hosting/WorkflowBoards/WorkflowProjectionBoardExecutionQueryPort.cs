using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Hosting.WorkflowBoards;

internal sealed class WorkflowProjectionBoardExecutionQueryPort : IWorkflowBoardExecutionQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionBoardDocument, string> _documentReader;
    private readonly IServiceRunQueryPort? _serviceRunQueryPort;

    public WorkflowProjectionBoardExecutionQueryPort(
        IProjectionDocumentReader<WorkflowExecutionBoardDocument, string> documentReader,
        IServiceRunQueryPort? serviceRunQueryPort = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _serviceRunQueryPort = serviceRunQueryPort;
    }

    public async Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
        WorkflowBoardExecutionLookup lookup,
        CancellationToken ct = default)
    {
        var actorId = lookup.ActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var direct = await ReadBoardDocumentAsync(actorId, lookup.ScopeId, ct);
            if (direct != null)
                return Map(direct);
        }

        var latestRun = await ReadLatestServiceRunAsync(lookup, ct);
        if (latestRun == null)
            return Unavailable();

        foreach (var runActorId in ResolveRunActorIds(latestRun))
        {
            var document = await ReadBoardDocumentAsync(runActorId, lookup.ScopeId, ct);
            if (document != null)
                return Map(document);
        }

        return Unavailable();
    }

    private async Task<WorkflowExecutionBoardDocument?> ReadBoardDocumentAsync(
        string actorId,
        string scopeId,
        CancellationToken ct)
    {
        var document = await _documentReader.GetAsync(actorId, ct);
        if (document == null)
            return null;

        return string.Equals(document.ScopeId ?? string.Empty, scopeId, StringComparison.Ordinal)
            ? document
            : null;
    }

    private async Task<ServiceRunSnapshot?> ReadLatestServiceRunAsync(
        WorkflowBoardExecutionLookup lookup,
        CancellationToken ct)
    {
        if (_serviceRunQueryPort == null)
            return null;

        var serviceId = lookup.PublishedServiceId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serviceId))
            return null;

        var runs = await _serviceRunQueryPort.ListAsync(
            new ServiceRunQuery(lookup.ScopeId, serviceId, Take: 1),
            ct);
        return runs.FirstOrDefault();
    }

    private static IEnumerable<string> ResolveRunActorIds(ServiceRunSnapshot run)
    {
        foreach (var candidate in new[] { run.TargetActorId, run.ActorId, run.RunId })
        {
            var actorId = candidate?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(actorId))
                yield return actorId;
        }
    }

    private static WorkflowBoardExecutionSnapshot Map(WorkflowExecutionBoardDocument document)
    {
        var completedNodes = document.NodeEntries
            .Where(static node => node.Status == WorkflowExecutionBoardNodeStatus.Completed)
            .OrderBy(static node => node.Order)
            .Select(static node => new WorkflowBoardCompletedNode(
                NormalizeNodeId(node.NodeId),
                ResolveNodeName(node),
                node.CompletedAt,
                OptionalDuration(node.DurationMs)))
            .ToArray();
        var pendingNodes = document.NodeEntries
            .Where(static node => node.Status is WorkflowExecutionBoardNodeStatus.Waiting or WorkflowExecutionBoardNodeStatus.Pending)
            .OrderBy(static node => node.Order)
            .Select(static node => new WorkflowBoardPendingNode(
                NormalizeNodeId(node.NodeId),
                ResolveNodeName(node),
                MapPendingStatus(node.Status)))
            .ToArray();
        var failedNodes = document.NodeEntries
            .Where(static node => node.Status == WorkflowExecutionBoardNodeStatus.Failed)
            .OrderBy(static node => node.Order)
            .Select(static node => new WorkflowBoardFailedNode(
                NormalizeNodeId(node.NodeId),
                ResolveNodeName(node),
                node.CompletedAt))
            .ToArray();
        var currentNode = ResolveCurrentNode(document);

        return new WorkflowBoardExecutionSnapshot(
            WorkflowBoardExecutionAvailability.Available,
            completedNodes,
            pendingNodes,
            failedNodes)
        {
            CurrentExecutionId = string.IsNullOrWhiteSpace(document.RunId) ? null : document.RunId,
            ExecutionStatus = MapExecutionStatus(document.CompletionStatus),
            CurrentNode = currentNode,
            LastNodeUpdatedAt = document.LastNodeUpdatedAt,
            Summary = new WorkflowBoardExecutionSummary(
                document.Summary.CompletedSteps,
                document.Summary.RunningNodes,
                document.Summary.WaitingOrPendingNodes,
                document.Summary.FailedNodes,
                document.Summary.HasDefinitionStepCount
                    ? document.Summary.DefinitionStepCount
                    : null),
            Revision = $"state-version-{document.StateVersion}:event-{document.LastEventId ?? string.Empty}",
        };
    }

    private static WorkflowBoardCurrentNode? ResolveCurrentNode(
        WorkflowExecutionBoardDocument document)
    {
        var currentNodeId = document.CurrentNodeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentNodeId))
            return null;

        var node = document.NodeEntries.FirstOrDefault(entry =>
            string.Equals(entry.NodeId ?? string.Empty, currentNodeId, StringComparison.Ordinal));
        if (node == null)
            return null;

        return new WorkflowBoardCurrentNode(
            NormalizeNodeId(node.NodeId),
            ResolveNodeName(node),
            MapCurrentStatus(node.Status),
            node.RequestedAt,
            node.UpdatedAt,
            OptionalDuration(node.DurationMs));
    }

    private static WorkflowBoardExecutionSnapshot Unavailable() =>
        new(
            WorkflowBoardExecutionAvailability.Unavailable,
            Array.Empty<WorkflowBoardCompletedNode>(),
            Array.Empty<WorkflowBoardPendingNode>(),
            Array.Empty<WorkflowBoardFailedNode>());

    private static WorkflowBoardMemberExecutionStatus MapExecutionStatus(
        WorkflowExecutionBoardCompletionStatus status) =>
        status switch
        {
            WorkflowExecutionBoardCompletionStatus.Running => WorkflowBoardMemberExecutionStatus.Running,
            WorkflowExecutionBoardCompletionStatus.WaitingForSignal => WorkflowBoardMemberExecutionStatus.Waiting,
            WorkflowExecutionBoardCompletionStatus.Failed => WorkflowBoardMemberExecutionStatus.Failed,
            WorkflowExecutionBoardCompletionStatus.Completed => WorkflowBoardMemberExecutionStatus.Completed,
            WorkflowExecutionBoardCompletionStatus.Stopped => WorkflowBoardMemberExecutionStatus.Stopped,
            _ => WorkflowBoardMemberExecutionStatus.Unknown,
        };

    private static WorkflowBoardCurrentNodeStatus MapCurrentStatus(WorkflowExecutionBoardNodeStatus status) =>
        status switch
        {
            WorkflowExecutionBoardNodeStatus.Running => WorkflowBoardCurrentNodeStatus.Running,
            WorkflowExecutionBoardNodeStatus.Waiting => WorkflowBoardCurrentNodeStatus.Waiting,
            WorkflowExecutionBoardNodeStatus.Pending => WorkflowBoardCurrentNodeStatus.Pending,
            WorkflowExecutionBoardNodeStatus.Failed => WorkflowBoardCurrentNodeStatus.Failed,
            WorkflowExecutionBoardNodeStatus.Completed => WorkflowBoardCurrentNodeStatus.Completed,
            _ => WorkflowBoardCurrentNodeStatus.Unknown,
        };

    private static WorkflowBoardPendingNodeStatus MapPendingStatus(WorkflowExecutionBoardNodeStatus status) =>
        status switch
        {
            WorkflowExecutionBoardNodeStatus.Waiting => WorkflowBoardPendingNodeStatus.Waiting,
            WorkflowExecutionBoardNodeStatus.Pending => WorkflowBoardPendingNodeStatus.Pending,
            _ => WorkflowBoardPendingNodeStatus.Unknown,
        };

    private static long? OptionalDuration(long durationMs) =>
        durationMs > 0 ? durationMs : null;

    private static string ResolveNodeName(WorkflowExecutionBoardNodeReadModel node)
    {
        var name = node.Name?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(name)
            ? NormalizeNodeId(node.NodeId)
            : name;
    }

    private static string NormalizeNodeId(string? nodeId) =>
        nodeId?.Trim() ?? string.Empty;
}
