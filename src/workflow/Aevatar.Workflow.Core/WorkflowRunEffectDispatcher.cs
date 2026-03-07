using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunEffectDispatcher
{
    private readonly Func<string> _actorIdAccessor;
    private readonly Func<string> _runIdAccessor;
    private readonly Func<WorkflowDefinition?> _compiledWorkflowAccessor;
    private readonly IActorRuntime _runtime;
    private readonly Func<Type> _resolveRoleAgentType;
    private readonly Func<string, string> _buildChildActorId;
    private readonly Func<RoleDefinition, EventEnvelope> _createRoleAgentInitializeEnvelope;
    private readonly Func<string, TimeSpan, IMessage, IReadOnlyDictionary<string, string>, CancellationToken, Task> _scheduleSelfDurableTimeoutAsync;
    private readonly Func<string, CancellationToken, Task<string>> _resolveWorkflowYamlAsync;
    private readonly Func<string, string, EventEnvelope> _createWorkflowDefinitionBindEnvelope;

    public WorkflowRunEffectDispatcher(
        Func<string> actorIdAccessor,
        Func<string> runIdAccessor,
        Func<WorkflowDefinition?> compiledWorkflowAccessor,
        IActorRuntime runtime,
        Func<Type> resolveRoleAgentType,
        Func<string, string> buildChildActorId,
        Func<RoleDefinition, EventEnvelope> createRoleAgentInitializeEnvelope,
        Func<string, TimeSpan, IMessage, IReadOnlyDictionary<string, string>, CancellationToken, Task> scheduleSelfDurableTimeoutAsync,
        Func<string, CancellationToken, Task<string>> resolveWorkflowYamlAsync,
        Func<string, string, EventEnvelope> createWorkflowDefinitionBindEnvelope)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _runIdAccessor = runIdAccessor ?? throw new ArgumentNullException(nameof(runIdAccessor));
        _compiledWorkflowAccessor = compiledWorkflowAccessor ?? throw new ArgumentNullException(nameof(compiledWorkflowAccessor));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _resolveRoleAgentType = resolveRoleAgentType ?? throw new ArgumentNullException(nameof(resolveRoleAgentType));
        _buildChildActorId = buildChildActorId ?? throw new ArgumentNullException(nameof(buildChildActorId));
        _createRoleAgentInitializeEnvelope = createRoleAgentInitializeEnvelope ?? throw new ArgumentNullException(nameof(createRoleAgentInitializeEnvelope));
        _scheduleSelfDurableTimeoutAsync = scheduleSelfDurableTimeoutAsync ?? throw new ArgumentNullException(nameof(scheduleSelfDurableTimeoutAsync));
        _resolveWorkflowYamlAsync = resolveWorkflowYamlAsync ?? throw new ArgumentNullException(nameof(resolveWorkflowYamlAsync));
        _createWorkflowDefinitionBindEnvelope = createWorkflowDefinitionBindEnvelope ?? throw new ArgumentNullException(nameof(createWorkflowDefinitionBindEnvelope));
    }

    public async Task EnsureAgentTreeAsync(CancellationToken ct)
    {
        var compiledWorkflow = _compiledWorkflowAccessor();
        if (compiledWorkflow == null)
            return;

        var roleAgentType = _resolveRoleAgentType();
        if (!typeof(IRoleAgent).IsAssignableFrom(roleAgentType))
            throw new InvalidOperationException($"Role agent type '{roleAgentType.FullName}' does not implement IRoleAgent.");

        foreach (var role in compiledWorkflow.Roles)
        {
            var childActorId = _buildChildActorId(role.Id);
            var actor = await _runtime.GetAsync(childActorId) ?? await _runtime.CreateAsync(roleAgentType, childActorId, ct);
            await _runtime.LinkAsync(_actorIdAccessor(), actor.Id, ct);
            await actor.HandleEventAsync(_createRoleAgentInitializeEnvelope(role), ct);
        }
    }

    public async Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflow.semantic_generation"] = semanticGeneration.ToString(CultureInfo.InvariantCulture),
            ["workflow.run_id"] = _runIdAccessor(),
            ["workflow.step_id"] = stepId,
            ["workflow.callback_kind"] = kind,
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
            metadata["workflow.session_id"] = sessionId;

        await _scheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, metadata, ct);
    }

    public async Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct)
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<WorkflowRunGAgent>(actorId, ct);
    }

    public Task LinkChildAsync(string childActorId, CancellationToken ct) =>
        _runtime.LinkAsync(_actorIdAccessor(), childActorId, ct);

    public async Task CleanupChildWorkflowAsync(string childActorId, CancellationToken ct)
    {
        await _runtime.UnlinkAsync(childActorId, ct);
        await _runtime.DestroyAsync(childActorId, ct);
    }

    public Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct) =>
        _resolveWorkflowYamlAsync(workflowName, ct);

    public EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName) =>
        _createWorkflowDefinitionBindEnvelope(workflowYaml, workflowName);

    public EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role) =>
        _createRoleAgentInitializeEnvelope(role);
}
