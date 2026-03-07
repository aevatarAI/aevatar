using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunRuntimeContext
{
    private readonly Func<string> _actorIdAccessor;
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowDefinition?> _compiledWorkflowAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<Exception?, string, object?[], Task> _logWarningAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;

    public WorkflowRunRuntimeContext(
        Func<string> actorIdAccessor,
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowDefinition?> compiledWorkflowAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<Exception?, string, object?[], Task> logWarningAsync,
        WorkflowRunEffectDispatcher effectDispatcher)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _compiledWorkflowAccessor = compiledWorkflowAccessor ?? throw new ArgumentNullException(nameof(compiledWorkflowAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _logWarningAsync = logWarningAsync ?? throw new ArgumentNullException(nameof(logWarningAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
    }

    public string ActorId => _actorIdAccessor();

    public WorkflowRunState State => _stateAccessor();

    public string RunId => State.RunId;

    public WorkflowDefinition? CompiledWorkflow => _compiledWorkflowAccessor();

    public Task PersistStateAsync(WorkflowRunState next, CancellationToken ct) =>
        _persistStateAsync(next, ct);

    public Task PublishAsync(IMessage evt, EventDirection direction, CancellationToken ct) =>
        _publishAsync(evt, direction, ct);

    public Task SendToAsync(string targetActorId, IMessage evt, CancellationToken ct) =>
        _sendToAsync(targetActorId, evt, ct);

    public Task LogWarningAsync(Exception? ex, string message, params object?[] args) =>
        _logWarningAsync(ex, message, args);

    public Task EnsureAgentTreeAsync(CancellationToken ct) =>
        _effectDispatcher.EnsureAgentTreeAsync(ct);

    public Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct) =>
        _effectDispatcher.ScheduleWorkflowCallbackAsync(
            callbackId,
            dueTime,
            evt,
            semanticGeneration,
            stepId,
            sessionId,
            kind,
            ct);

    public Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct) =>
        _effectDispatcher.ResolveOrCreateSubWorkflowRunActorAsync(actorId, ct);

    public Task LinkChildAsync(string childActorId, CancellationToken ct) =>
        _effectDispatcher.LinkChildAsync(childActorId, ct);

    public Task CleanupChildWorkflowAsync(string childActorId, CancellationToken ct) =>
        _effectDispatcher.CleanupChildWorkflowAsync(childActorId, ct);

    public Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct) =>
        _effectDispatcher.ResolveWorkflowYamlAsync(workflowName, ct);

    public EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName) =>
        _effectDispatcher.CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName);

    public EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role) =>
        _effectDispatcher.CreateRoleAgentInitializeEnvelope(role);
}
