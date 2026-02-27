using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Sisyphus.Application.Tests.Fakes;

internal sealed class FakeWorkflowQueryService : IWorkflowExecutionQueryApplicationService
{
    /// <summary>YAML content returned by <see cref="GetWorkflowYaml"/>.</summary>
    public string? WorkflowYaml { get; set; }

    public bool ActorQueryEnabled => false;

    public string? GetWorkflowYaml(string name) => WorkflowYaml;

    public IReadOnlyList<string> ListWorkflows() => [];

    public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

    public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default) =>
        Task.FromResult<WorkflowActorSnapshot?>(null);

    public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId, int take = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);

    public Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
        string actorId, int take = 200, WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);

    public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId, int depth = 2, int take = 200,
        WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
        Task.FromResult(new WorkflowActorGraphSubgraph());

    public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId, int depth = 2, int take = 200,
        WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
        Task.FromResult<WorkflowActorGraphEnrichedSnapshot?>(null);
}
