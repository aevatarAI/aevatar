using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowExecutionBridgeModule : IEventModule<IEventHandlerContext>
{
    private readonly IReadOnlyList<IEventModule<IWorkflowExecutionContext>> _executors;
    private readonly IWorkflowExecutionStateHost _stateHost;

    public WorkflowExecutionBridgeModule(
        IEnumerable<IEventModule<IWorkflowExecutionContext>> executors,
        IWorkflowExecutionStateHost stateHost)
    {
        _stateHost = stateHost ?? throw new ArgumentNullException(nameof(stateHost));
        _executors = executors
            .OrderBy(x => x.Priority)
            .ToArray();
    }

    public string Name => "workflow_execution_bridge";

    public int Priority => 0;

    public bool CanHandle(EventEnvelope envelope) =>
        _executors.Any(x => x.CanHandle(envelope));

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var workflowContext = WorkflowExecutionContextAdapter.Create(ctx, _stateHost);
        foreach (var executor in _executors)
        {
            if (!executor.CanHandle(envelope))
                continue;

            await executor.HandleAsync(envelope, workflowContext, ct);
        }
    }
}
