using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunEffectPorts
{
    private readonly Func<CancellationToken, Task> _ensureAgentTreeAsync;
    private readonly Func<string, TimeSpan, IMessage, int, string, string?, string, CancellationToken, Task> _scheduleWorkflowCallbackAsync;
    private readonly Func<string, CancellationToken, Task<IActor>> _resolveOrCreateSubWorkflowRunActorAsync;
    private readonly Func<string, CancellationToken, Task> _linkChildAsync;
    private readonly Func<string, CancellationToken, Task> _cleanupChildWorkflowAsync;
    private readonly Func<string, CancellationToken, Task<string>> _resolveWorkflowYamlAsync;
    private readonly Func<string, string, EventEnvelope> _createWorkflowDefinitionBindEnvelope;
    private readonly Func<RoleDefinition, EventEnvelope> _createRoleAgentInitializeEnvelope;
    private readonly Func<StepDefinition, string, string, CancellationToken, Task> _dispatchWorkflowStepAsync;
    private readonly Func<string, string, string, string, string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task> _dispatchInternalStepAsync;
    private readonly Func<WorkflowWhileState, string, CancellationToken, Task> _dispatchWhileIterationAsync;
    private readonly Func<bool, string, string, CancellationToken, Task> _finalizeRunAsync;

    public WorkflowRunEffectPorts(
        Func<CancellationToken, Task> ensureAgentTreeAsync,
        Func<string, TimeSpan, IMessage, int, string, string?, string, CancellationToken, Task> scheduleWorkflowCallbackAsync,
        Func<string, CancellationToken, Task<IActor>> resolveOrCreateSubWorkflowRunActorAsync,
        Func<string, CancellationToken, Task> linkChildAsync,
        Func<string, CancellationToken, Task> cleanupChildWorkflowAsync,
        Func<string, CancellationToken, Task<string>> resolveWorkflowYamlAsync,
        Func<string, string, EventEnvelope> createWorkflowDefinitionBindEnvelope,
        Func<RoleDefinition, EventEnvelope> createRoleAgentInitializeEnvelope,
        Func<StepDefinition, string, string, CancellationToken, Task> dispatchWorkflowStepAsync,
        Func<string, string, string, string, string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task> dispatchInternalStepAsync,
        Func<WorkflowWhileState, string, CancellationToken, Task> dispatchWhileIterationAsync,
        Func<bool, string, string, CancellationToken, Task> finalizeRunAsync)
    {
        _ensureAgentTreeAsync = ensureAgentTreeAsync ?? throw new ArgumentNullException(nameof(ensureAgentTreeAsync));
        _scheduleWorkflowCallbackAsync = scheduleWorkflowCallbackAsync ?? throw new ArgumentNullException(nameof(scheduleWorkflowCallbackAsync));
        _resolveOrCreateSubWorkflowRunActorAsync = resolveOrCreateSubWorkflowRunActorAsync ?? throw new ArgumentNullException(nameof(resolveOrCreateSubWorkflowRunActorAsync));
        _linkChildAsync = linkChildAsync ?? throw new ArgumentNullException(nameof(linkChildAsync));
        _cleanupChildWorkflowAsync = cleanupChildWorkflowAsync ?? throw new ArgumentNullException(nameof(cleanupChildWorkflowAsync));
        _resolveWorkflowYamlAsync = resolveWorkflowYamlAsync ?? throw new ArgumentNullException(nameof(resolveWorkflowYamlAsync));
        _createWorkflowDefinitionBindEnvelope = createWorkflowDefinitionBindEnvelope ?? throw new ArgumentNullException(nameof(createWorkflowDefinitionBindEnvelope));
        _createRoleAgentInitializeEnvelope = createRoleAgentInitializeEnvelope ?? throw new ArgumentNullException(nameof(createRoleAgentInitializeEnvelope));
        _dispatchWorkflowStepAsync = dispatchWorkflowStepAsync ?? throw new ArgumentNullException(nameof(dispatchWorkflowStepAsync));
        _dispatchInternalStepAsync = dispatchInternalStepAsync ?? throw new ArgumentNullException(nameof(dispatchInternalStepAsync));
        _dispatchWhileIterationAsync = dispatchWhileIterationAsync ?? throw new ArgumentNullException(nameof(dispatchWhileIterationAsync));
        _finalizeRunAsync = finalizeRunAsync ?? throw new ArgumentNullException(nameof(finalizeRunAsync));
    }

    public Task EnsureAgentTreeAsync(CancellationToken ct) =>
        _ensureAgentTreeAsync(ct);

    public Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct) =>
        _scheduleWorkflowCallbackAsync(callbackId, dueTime, evt, semanticGeneration, stepId, sessionId, kind, ct);

    public Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct) =>
        _resolveOrCreateSubWorkflowRunActorAsync(actorId, ct);

    public Task LinkChildAsync(string childActorId, CancellationToken ct) =>
        _linkChildAsync(childActorId, ct);

    public Task CleanupChildWorkflowAsync(string childActorId, CancellationToken ct) =>
        _cleanupChildWorkflowAsync(childActorId, ct);

    public Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct) =>
        _resolveWorkflowYamlAsync(workflowName, ct);

    public EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName) =>
        _createWorkflowDefinitionBindEnvelope(workflowYaml, workflowName);

    public EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role) =>
        _createRoleAgentInitializeEnvelope(role);

    public Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct) =>
        _dispatchWorkflowStepAsync(step, input, runId, ct);

    public Task DispatchInternalStepAsync(
        string runId,
        string parentStepId,
        string stepId,
        string stepType,
        string input,
        string targetRole,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct) =>
        _dispatchInternalStepAsync(runId, parentStepId, stepId, stepType, input, targetRole, parameters, ct);

    public Task DispatchWhileIterationAsync(
        WorkflowWhileState state,
        string input,
        CancellationToken ct) =>
        _dispatchWhileIterationAsync(state, input, ct);

    public Task FinalizeRunAsync(bool success, string output, string error, CancellationToken ct) =>
        _finalizeRunAsync(success, output, error, ct);
}
