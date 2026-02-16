using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Orchestration;

public sealed class WorkflowExecutionRunOrchestrator : IWorkflowExecutionRunOrchestrator
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowExecutionTopologyResolver _topologyResolver;
    private readonly WorkflowRunOrchestrationOptions _orchestrationOptions;
    private readonly ILogger<WorkflowExecutionRunOrchestrator> _logger;

    public WorkflowExecutionRunOrchestrator(
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowExecutionTopologyResolver topologyResolver,
        WorkflowRunOrchestrationOptions orchestrationOptions,
        ILogger<WorkflowExecutionRunOrchestrator> logger)
    {
        _projectionPort = projectionPort;
        _topologyResolver = topologyResolver;
        _orchestrationOptions = orchestrationOptions;
        _logger = logger;
    }

    public async Task<WorkflowProjectionRun> StartAsync(
        string actorId,
        string workflowName,
        string prompt,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        var session = await _projectionPort.StartAsync(actorId, workflowName, prompt, sink, ct);
        return new WorkflowProjectionRun(session);
    }

    public async Task<WorkflowProjectionFinalizeResult> FinalizeAsync(
        WorkflowProjectionRun projectionRun,
        IActorRuntime runtime,
        string actorId,
        CancellationToken ct = default)
    {
        var runId = projectionRun.RunId;
        var completionStatus = await _projectionPort.WaitForRunProjectionCompletionStatusAsync(runId, ct: ct);

        if (completionStatus == WorkflowProjectionCompletionStatus.TimedOut)
        {
            _logger.LogWarning(
                "Projection completion signal timeout for run {RunId}; wait grace window before finalize.",
                runId);

            completionStatus = await _projectionPort.WaitForRunProjectionCompletionStatusAsync(
                runId,
                timeoutOverride: TimeSpan.FromMilliseconds(Math.Max(1, _orchestrationOptions.RunProjectionFinalizeGraceTimeoutMs)),
                ct: ct);
        }

        if (completionStatus != WorkflowProjectionCompletionStatus.Completed)
        {
            _logger.LogWarning(
                "Projection completion status for run {RunId}: {Status}. Continue with finalize/query.",
                runId,
                completionStatus);
        }

        var topology = await _topologyResolver.ResolveAsync(runtime, actorId, ct);
        var report = await _projectionPort.CompleteAsync(projectionRun.Session, topology, ct);
        if (report == null && _projectionPort.EnableRunQueryEndpoints)
            report = await _projectionPort.GetRunAsync(runId, ct);
        if (report != null)
            report.CompletionStatus = ToRunCompletionStatus(completionStatus);

        projectionRun.WorkflowExecutionReport = report;
        return new WorkflowProjectionFinalizeResult(completionStatus, report);
    }

    public async Task RollbackAsync(
        WorkflowProjectionRun projectionRun,
        CancellationToken ct = default)
    {
        try
        {
            _ = await _projectionPort.CompleteAsync(projectionRun.Session, [], ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to complete run-event projection lifecycle.");
        }
    }

    private static WorkflowRunCompletionStatus ToRunCompletionStatus(WorkflowProjectionCompletionStatus status) =>
        status switch
        {
            WorkflowProjectionCompletionStatus.Completed => WorkflowRunCompletionStatus.Completed,
            WorkflowProjectionCompletionStatus.TimedOut => WorkflowRunCompletionStatus.TimedOut,
            WorkflowProjectionCompletionStatus.Failed => WorkflowRunCompletionStatus.Failed,
            WorkflowProjectionCompletionStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            WorkflowProjectionCompletionStatus.NotFound => WorkflowRunCompletionStatus.NotFound,
            WorkflowProjectionCompletionStatus.Disabled => WorkflowRunCompletionStatus.Disabled,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
}
