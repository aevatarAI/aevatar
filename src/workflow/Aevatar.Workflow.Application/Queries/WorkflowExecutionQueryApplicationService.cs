using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Queries;

public sealed class WorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowDefinitionRegistry _workflowRegistry;
    private readonly IWorkflowExecutionProjectionService _projectionService;

    public WorkflowExecutionQueryApplicationService(
        IActorRuntime runtime,
        IWorkflowDefinitionRegistry workflowRegistry,
        IWorkflowExecutionProjectionService projectionService)
    {
        _runtime = runtime;
        _workflowRegistry = workflowRegistry;
        _projectionService = projectionService;
    }

    public bool RunQueryEnabled => _projectionService.EnableRunQueryEndpoints;

    public async Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actors = await _runtime.GetAllAsync();
        var result = new List<WorkflowAgentSummary>(actors.Count);

        foreach (var actor in actors)
        {
            var description = await actor.Agent.GetDescriptionAsync();
            result.Add(new WorkflowAgentSummary(actor.Id, actor.Agent.GetType().Name, description));
        }

        return result;
    }

    public IReadOnlyList<string> ListWorkflows() => _workflowRegistry.GetNames();

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!RunQueryEnabled)
            return [];

        var reports = await _projectionService.ListRunsAsync(take, ct);
        return reports.Select(MapSummary).ToList();
    }

    public async Task<WorkflowRunReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!RunQueryEnabled)
            return null;

        var report = await _projectionService.GetRunAsync(runId, ct);
        return report == null ? null : MapReport(report);
    }

    private static WorkflowRunSummary MapSummary(WorkflowExecutionReport report)
    {
        return new WorkflowRunSummary(
            report.RunId,
            report.WorkflowName,
            report.RootActorId,
            report.StartedAt,
            report.EndedAt,
            report.DurationMs,
            report.Success,
            report.Summary.TotalSteps,
            ParseProjectionScope(report.ProjectionScope),
            ParseCompletionStatus(report.CompletionStatus));
    }

    private static WorkflowRunReport MapReport(WorkflowExecutionReport source)
    {
        return new WorkflowRunReport
        {
            ReportVersion = source.ReportVersion,
            ProjectionScope = ParseProjectionScope(source.ProjectionScope),
            TopologySource = ParseTopologySource(source.TopologySource),
            CompletionStatus = ParseCompletionStatus(source.CompletionStatus),
            WorkflowName = source.WorkflowName,
            RootActorId = source.RootActorId,
            RunId = source.RunId,
            StartedAt = source.StartedAt,
            EndedAt = source.EndedAt,
            DurationMs = source.DurationMs,
            Success = source.Success,
            Input = source.Input,
            FinalOutput = source.FinalOutput,
            FinalError = source.FinalError,
            Topology = source.Topology
                .Select(x => new WorkflowRunTopologyEdge(x.Parent, x.Child))
                .ToList(),
            Steps = source.Steps
                .Select(x => new WorkflowRunStepTrace
                {
                    StepId = x.StepId,
                    StepType = x.StepType,
                    RunId = x.RunId,
                    TargetRole = x.TargetRole,
                    RequestedAt = x.RequestedAt,
                    CompletedAt = x.CompletedAt,
                    Success = x.Success,
                    WorkerId = x.WorkerId,
                    OutputPreview = x.OutputPreview,
                    Error = x.Error,
                    RequestParameters = new Dictionary<string, string>(x.RequestParameters, StringComparer.Ordinal),
                    CompletionMetadata = new Dictionary<string, string>(x.CompletionMetadata, StringComparer.Ordinal),
                })
                .ToList(),
            RoleReplies = source.RoleReplies
                .Select(x => new WorkflowRunRoleReply
                {
                    Timestamp = x.Timestamp,
                    RoleId = x.RoleId,
                    SessionId = x.SessionId,
                    Content = x.Content,
                    ContentLength = x.ContentLength,
                })
                .ToList(),
            Timeline = source.Timeline
                .Select(x => new WorkflowRunTimelineEvent
                {
                    Timestamp = x.Timestamp,
                    Stage = x.Stage,
                    Message = x.Message,
                    AgentId = x.AgentId,
                    StepId = x.StepId,
                    StepType = x.StepType,
                    EventType = x.EventType,
                    Data = new Dictionary<string, string>(x.Data, StringComparer.Ordinal),
                })
                .ToList(),
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = source.Summary.TotalSteps,
                RequestedSteps = source.Summary.RequestedSteps,
                CompletedSteps = source.Summary.CompletedSteps,
                RoleReplyCount = source.Summary.RoleReplyCount,
                StepTypeCounts = new Dictionary<string, int>(source.Summary.StepTypeCounts, StringComparer.Ordinal),
            },
        };
    }

    private static WorkflowRunProjectionScope ParseProjectionScope(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "actor_shared" => WorkflowRunProjectionScope.ActorShared,
            "run_isolated" => WorkflowRunProjectionScope.RunIsolated,
            _ => WorkflowRunProjectionScope.Unknown,
        };
    }

    private static WorkflowRunTopologySource ParseTopologySource(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "runtime_snapshot" => WorkflowRunTopologySource.RuntimeSnapshot,
            _ => WorkflowRunTopologySource.Unknown,
        };
    }

    private static WorkflowRunCompletionStatus ParseCompletionStatus(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "running" => WorkflowRunCompletionStatus.Running,
            "completed" => WorkflowRunCompletionStatus.Completed,
            "timed_out" => WorkflowRunCompletionStatus.TimedOut,
            "failed" => WorkflowRunCompletionStatus.Failed,
            "stopped" => WorkflowRunCompletionStatus.Stopped,
            "not_found" => WorkflowRunCompletionStatus.NotFound,
            "disabled" => WorkflowRunCompletionStatus.Disabled,
            _ => WorkflowRunCompletionStatus.Unknown,
        };
    }
}
