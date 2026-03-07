using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunReadContext
{
    private readonly Func<string> _actorIdAccessor;
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowDefinition?> _compiledWorkflowAccessor;

    public WorkflowRunReadContext(
        Func<string> actorIdAccessor,
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowDefinition?> compiledWorkflowAccessor)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _compiledWorkflowAccessor = compiledWorkflowAccessor ?? throw new ArgumentNullException(nameof(compiledWorkflowAccessor));
    }

    public string ActorId => _actorIdAccessor();

    public WorkflowRunState State => _stateAccessor();

    public string RunId => State.RunId;

    public WorkflowDefinition? CompiledWorkflow => _compiledWorkflowAccessor();
}
