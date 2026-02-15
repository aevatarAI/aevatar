using Aevatar.CQRS.Projection.WorkflowExecution;
using Aevatar.CQRS.Projection.WorkflowExecution.ReadModels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Presentation.AGUI.Adapter.WorkflowExecution;
using Microsoft.Extensions.Logging;

namespace Aevatar.Host.Api.Orchestration;

public interface IWorkflowExecutionRunOrchestrator
{
    Task<WorkflowProjectionRun> StartAsync(
        string actorId,
        string workflowName,
        string prompt,
        IAGUIEventSink sink,
        CancellationToken ct = default);

    Task<WorkflowProjectionFinalizeResult> FinalizeAsync(
        WorkflowProjectionRun projectionRun,
        IActorRuntime runtime,
        string actorId,
        CancellationToken ct = default);

    Task RollbackAsync(
        WorkflowProjectionRun projectionRun,
        CancellationToken ct = default);
}

public sealed class WorkflowExecutionRunOrchestrator : IWorkflowExecutionRunOrchestrator
{
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly ILogger<WorkflowExecutionRunOrchestrator> _logger;

    public WorkflowExecutionRunOrchestrator(
        IWorkflowExecutionProjectionService projectionService,
        ILogger<WorkflowExecutionRunOrchestrator> logger)
    {
        _projectionService = projectionService;
        _logger = logger;
    }

    public async Task<WorkflowProjectionRun> StartAsync(
        string actorId,
        string workflowName,
        string prompt,
        IAGUIEventSink sink,
        CancellationToken ct = default)
    {
        var session = await _projectionService.StartAsync(actorId, workflowName, prompt, ct);
        session.Context?.SetAGUIEventSink(sink);
        return new WorkflowProjectionRun(session);
    }

    public async Task<WorkflowProjectionFinalizeResult> FinalizeAsync(
        WorkflowProjectionRun projectionRun,
        IActorRuntime runtime,
        string actorId,
        CancellationToken ct = default)
    {
        var runId = projectionRun.RunId;
        var projectionCompleted = await _projectionService.WaitForRunProjectionCompletedAsync(runId, ct);

        if (!projectionCompleted)
        {
            _logger.LogWarning(
                "Projection completion signal timeout for run {RunId}; continue with finalize/query.",
                runId);
        }

        var topology = await BuildTopologyAsync(runtime, actorId);
        var report = await _projectionService.CompleteAsync(projectionRun.Session, topology, ct);
        if (report == null && _projectionService.EnableRunQueryEndpoints)
            report = await _projectionService.GetRunAsync(runId, ct);

        projectionRun.WorkflowExecutionReport = report;
        return new WorkflowProjectionFinalizeResult(projectionCompleted, report);
    }

    public async Task RollbackAsync(
        WorkflowProjectionRun projectionRun,
        CancellationToken ct = default)
    {
        try
        {
            _ = await _projectionService.CompleteAsync(projectionRun.Session, [], ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to complete AGUI event-stream projection lifecycle.");
        }
    }

    private static async Task<List<WorkflowExecutionTopologyEdge>> BuildTopologyAsync(
        IActorRuntime runtime,
        string rootActorId)
    {
        var allActors = await runtime.GetAllAsync();
        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var actor in allActors)
        {
            var parent = await actor.GetParentIdAsync();
            if (!string.IsNullOrWhiteSpace(parent))
            {
                if (!childrenByParent.TryGetValue(parent, out var children))
                {
                    children = [];
                    childrenByParent[parent] = children;
                }

                children.Add(actor.Id);
            }
        }

        var topology = new List<WorkflowExecutionTopologyEdge>();
        if (string.IsNullOrWhiteSpace(rootActorId))
            return topology;

        var visited = new HashSet<string>(StringComparer.Ordinal) { rootActorId };
        var queue = new Queue<string>();
        queue.Enqueue(rootActorId);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children))
                continue;

            foreach (var child in children)
            {
                topology.Add(new WorkflowExecutionTopologyEdge(parent, child));
                if (visited.Add(child))
                    queue.Enqueue(child);
            }
        }

        return topology;
    }
}

public sealed class WorkflowProjectionRun
{
    public WorkflowProjectionRun(WorkflowExecutionProjectionSession session) =>
        Session = session;

    public WorkflowExecutionProjectionSession Session { get; }

    public string RunId => Session.RunId;

    public WorkflowExecutionReport? WorkflowExecutionReport { get; set; }
}

public sealed record WorkflowProjectionFinalizeResult(
    bool ProjectionCompleted,
    WorkflowExecutionReport? WorkflowExecutionReport);
