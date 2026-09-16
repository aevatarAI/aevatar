using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter115/cluster-3):
//   Old pattern: module contexts could only access process-local runtime context facts.
//   New principle: module contexts expose the actor state host so facades can resolve
//                  typed execution context state without query/replay side reads.
internal sealed class WorkflowExecutionContextAdapter :
    IWorkflowExecutionContext,
    IWorkflowExecutionRuntimeContextAccessor,
    IWorkflowExecutionStateHostAccessor
{
    private readonly IEventHandlerContext _inner;
    private readonly IWorkflowExecutionStateHost _stateHost;

    private WorkflowExecutionContextAdapter(
        IEventHandlerContext inner,
        IWorkflowExecutionStateHost stateHost)
    {
        _inner = inner;
        _stateHost = stateHost;
    }

    public EventEnvelope InboundEnvelope => _inner.InboundEnvelope;

    public string AgentId => _inner.AgentId;

    public string RunId => _stateHost.RunId;

    public string ScopeId => _stateHost.ScopeId;

    public string ScheduleId => _stateHost.ScheduleId;

    public string ToolCatalogPolicyVersion => _stateHost.ToolCatalogPolicyVersion;

    public WorkflowCallerNyxIdAuthority? CallerNyxIdAuthority
    {
        get
        {
            var source = _stateHost.ExecutionContextSnapshot.CallerCredential?.NyxIdAuthority;
            return WorkflowRunExecutionContextStateAccess.TryNormalizeCallerNyxIdAuthority(
                source,
                out var authority)
                ? authority
                : null;
        }
    }

    public WorkflowExecutionRuntimeContext RuntimeContext => _stateHost.RuntimeContext;

    public IWorkflowExecutionStateHost StateHost => _stateHost;

    public IServiceProvider Services => _inner.Services;

    public ILogger Logger => _inner.Logger;

    // Refactor (iter89/cluster-089-workflow-module-clock-state):
    //   Old: Workflow modules used process wall clock/Stopwatch directly.
    //   New: Modules consume the workflow execution context clock so tests
    //        and runtimes can inject business time and monotonic duration.
    public DateTimeOffset UtcNow => Clock.GetUtcNow();

    private TimeProvider Clock =>
        _inner.Services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;

    public static WorkflowExecutionContextAdapter Create(
        IEventHandlerContext context,
        IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stateHost);
        return new WorkflowExecutionContextAdapter(context, stateHost);
    }

    public long GetTimestamp() => Clock.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Clock.GetElapsedTime(startingTimestamp);

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var packed = _stateHost.GetExecutionState(scopeKey);
        var descriptor = new TState().Descriptor;
        if (packed == null || !packed.Is(descriptor))
            return new TState();

        return packed.Unpack<TState>() ?? new TState();
    }

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new()
    {
        var prefix = scopeKeyPrefix?.Trim() ?? string.Empty;
        var states = new List<KeyValuePair<string, TState>>();
        foreach (var (scopeKey, packed) in _stateHost.GetExecutionStates())
        {
            if (!string.IsNullOrEmpty(prefix) &&
                !scopeKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var descriptor = new TState().Descriptor;
            if (!packed.Is(descriptor))
                continue;

            states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
        }

        return states;
    }

    public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
        where TState : class, IMessage<TState>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(state);
        return _stateHost.UpsertExecutionStateAsync(scopeKey, Any.Pack(state), ct);
    }

    public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return _stateHost.ClearExecutionStateAsync(scopeKey, ct);
    }

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience direction = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        if (evt is StepCompletedEvent completion &&
            string.IsNullOrWhiteSpace(completion.ExecutionId) &&
            _inner.InboundEnvelope.Payload?.Is(StepRequestEvent.Descriptor) == true)
        {
            var request = _inner.InboundEnvelope.Payload.Unpack<StepRequestEvent>();
            if (string.Equals(completion.StepId, request.StepId, StringComparison.Ordinal) &&
                string.Equals(
                    WorkflowRunIdNormalizer.Normalize(completion.RunId),
                    WorkflowRunIdNormalizer.Normalize(request.RunId),
                    StringComparison.Ordinal))
            {
                completion.ExecutionId = request.ExecutionId;
            }
        }

        return _inner.PublishAsync(evt, direction, ct, options);
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage =>
        _inner.SendToAsync(targetActorId, evt, ct, options);

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default) =>
        _inner.ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, options, ct);

    public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
        _inner.CancelDurableCallbackAsync(lease, ct);
}
