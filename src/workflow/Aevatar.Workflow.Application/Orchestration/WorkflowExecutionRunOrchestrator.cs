using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Orchestration;

public sealed class WorkflowExecutionRunOrchestrator : IWorkflowExecutionRunOrchestrator
{
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly IWorkflowExecutionTopologyResolver _topologyResolver;
    private readonly WorkflowExecutionProjectionOptions _projectionOptions;
    private readonly ILogger<WorkflowExecutionRunOrchestrator> _logger;

    public WorkflowExecutionRunOrchestrator(
        IWorkflowExecutionProjectionService projectionService,
        IWorkflowExecutionTopologyResolver topologyResolver,
        WorkflowExecutionProjectionOptions projectionOptions,
        ILogger<WorkflowExecutionRunOrchestrator> logger)
    {
        _projectionService = projectionService;
        _topologyResolver = topologyResolver;
        _projectionOptions = projectionOptions;
        _logger = logger;
    }

    public async Task<WorkflowProjectionRun> StartAsync(
        string actorId,
        string workflowName,
        string prompt,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        var session = await _projectionService.StartAsync(actorId, workflowName, prompt, ct);
        session.Context?.SetRunEventSink(sink);
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
                timeoutOverride: TimeSpan.FromMilliseconds(Math.Max(1, _projectionOptions.RunProjectionFinalizeGraceTimeoutMs)),
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
            report.CompletionStatus = ToCompletionStatus(completionStatus);

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
            _logger.LogDebug(ex, "Failed to complete run-event projection lifecycle.");
        }
    }

    private static WorkflowExecutionCompletionStatus ToCompletionStatus(ProjectionRunCompletionStatus status) =>
        status switch
        {
            ProjectionRunCompletionStatus.Completed => WorkflowExecutionCompletionStatus.Completed,
            ProjectionRunCompletionStatus.TimedOut => WorkflowExecutionCompletionStatus.TimedOut,
            ProjectionRunCompletionStatus.Failed => WorkflowExecutionCompletionStatus.Failed,
            ProjectionRunCompletionStatus.Stopped => WorkflowExecutionCompletionStatus.Stopped,
            ProjectionRunCompletionStatus.NotFound => WorkflowExecutionCompletionStatus.NotFound,
            ProjectionRunCompletionStatus.Disabled => WorkflowExecutionCompletionStatus.Disabled,
            _ => WorkflowExecutionCompletionStatus.Unknown,
        };
}
