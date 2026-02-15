using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Orchestration;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Orchestration;

public sealed class WorkflowExecutionRunOrchestrator : IWorkflowExecutionRunOrchestrator
{
    private static readonly TimeSpan FinalizeGraceTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly IWorkflowExecutionTopologyResolver _topologyResolver;
    private readonly ILogger<WorkflowExecutionRunOrchestrator> _logger;

    public WorkflowExecutionRunOrchestrator(
        IWorkflowExecutionProjectionService projectionService,
        IWorkflowExecutionTopologyResolver topologyResolver,
        ILogger<WorkflowExecutionRunOrchestrator> logger)
    {
        _projectionService = projectionService;
        _topologyResolver = topologyResolver;
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
        var completionStatus = await _projectionService.WaitForRunProjectionCompletionStatusAsync(runId, ct: ct);

        if (completionStatus == ProjectionRunCompletionStatus.TimedOut)
        {
            _logger.LogWarning(
                "Projection completion signal timeout for run {RunId}; wait grace window before finalize.",
                runId);

            completionStatus = await _projectionService.WaitForRunProjectionCompletionStatusAsync(
                runId,
                timeoutOverride: FinalizeGraceTimeout,
                ct: ct);
        }

        if (completionStatus != ProjectionRunCompletionStatus.Completed)
        {
            _logger.LogWarning(
                "Projection completion status for run {RunId}: {Status}. Continue with finalize/query.",
                runId,
                completionStatus);
        }

        var topology = await _topologyResolver.ResolveAsync(runtime, actorId, ct);
        var report = await _projectionService.CompleteAsync(projectionRun.Session, topology, ct);
        if (report == null && _projectionService.EnableRunQueryEndpoints)
            report = await _projectionService.GetRunAsync(runId, ct);
        if (report != null)
            report.CompletionStatus = ToCompletionStatusName(completionStatus);

        projectionRun.WorkflowExecutionReport = report;
        return new WorkflowProjectionFinalizeResult(completionStatus, report);
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

    private static string ToCompletionStatusName(ProjectionRunCompletionStatus status) =>
        status switch
        {
            ProjectionRunCompletionStatus.Completed => "completed",
            ProjectionRunCompletionStatus.TimedOut => "timed_out",
            ProjectionRunCompletionStatus.Failed => "failed",
            ProjectionRunCompletionStatus.Stopped => "stopped",
            ProjectionRunCompletionStatus.NotFound => "not_found",
            ProjectionRunCompletionStatus.Disabled => "disabled",
            _ => "unknown",
        };
}
