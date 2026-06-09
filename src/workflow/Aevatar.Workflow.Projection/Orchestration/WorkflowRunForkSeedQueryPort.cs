using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowRunForkSeedQueryPort : IWorkflowRunForkSeedQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _currentStateReader;
    private readonly IWorkflowRunBindingReader _runBindingReader;
    private readonly WorkflowRunForkSeedReadModelMapper _mapper;
    private readonly bool _queryEnabled;

    public WorkflowRunForkSeedQueryPort(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> currentStateReader,
        IWorkflowRunBindingReader runBindingReader,
        WorkflowRunForkSeedReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _currentStateReader = currentStateReader ?? throw new ArgumentNullException(nameof(currentStateReader));
        _runBindingReader = runBindingReader ?? throw new ArgumentNullException(nameof(runBindingReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _queryEnabled = options == null || (options.Enabled && options.WorkflowActorCurrentStateQueryEnabled);
    }

    public async Task<WorkflowRunForkSeedView?> GetForkSeedAsync(
        string runId,
        CancellationToken ct = default)
    {
        if (!_queryEnabled || string.IsNullOrWhiteSpace(runId))
            return null;

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var bindings = await _runBindingReader.ListByRunIdAsync(normalizedRunId, take: 20, ct);
        foreach (var binding in bindings)
        {
            if (binding.ActorKind != WorkflowActorKind.Run || string.IsNullOrWhiteSpace(binding.ActorId))
                continue;

            var currentState = await _currentStateReader.GetAsync(binding.ActorId, ct);
            if (currentState == null)
                continue;

            return _mapper.ToSeedView(currentState);
        }

        return null;
    }
}
