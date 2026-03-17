using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _currentStateReader;
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly bool _enableActorQueryEndpoints;

    public WorkflowExecutionCurrentStateQueryPort(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> currentStateReader,
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        WorkflowExecutionReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _currentStateReader = currentStateReader ?? throw new ArgumentNullException(nameof(currentStateReader));
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _enableActorQueryEndpoints = options == null || (options.Enabled && options.EnableActorQueryEndpoints);
    }

    public bool EnableActorQueryEndpoints => _enableActorQueryEndpoints;

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        if (currentState == null)
            return null;

        var report = await _reportReader.GetAsync(actorId, ct);
        return _mapper.ToActorSnapshot(currentState, report);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var currentStates = await _currentStateReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        var snapshots = new List<WorkflowActorSnapshot>(currentStates.Items.Count);
        foreach (var currentState in currentStates.Items)
        {
            var report = await _reportReader.GetAsync(currentState.RootActorId, ct);
            snapshots.Add(_mapper.ToActorSnapshot(currentState, report));
        }

        return snapshots;
    }

    public async Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        return currentState == null ? null : _mapper.ToActorProjectionState(currentState);
    }
}
