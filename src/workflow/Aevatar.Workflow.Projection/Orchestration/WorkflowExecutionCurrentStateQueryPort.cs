using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _currentStateReader;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly bool _enableActorQueryEndpoints;

    public WorkflowExecutionCurrentStateQueryPort(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> currentStateReader,
        WorkflowExecutionReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _currentStateReader = currentStateReader ?? throw new ArgumentNullException(nameof(currentStateReader));
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
        return currentState == null ? null : _mapper.ToActorSnapshot(currentState);
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
        return currentStates.Items.Select(_mapper.ToActorSnapshot).ToList();
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
