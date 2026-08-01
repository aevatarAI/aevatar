using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Integration.Tests;

internal sealed class TestEventHandlerContext :
    IEventHandlerContext,
    IWorkflowExecutionContext,
    IWorkflowExecutionRuntimeContextAccessor,
    IWorkflowExecutionStateHostAccessor
{
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);

    public TestEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
    {
        Services = services;
        Agent = agent;
        Logger = logger;
        InboundEnvelope = new EventEnvelope();
    }

    public List<(IMessage evt, TopologyAudience direction)> Published { get; } = [];
    public List<(string targetActorId, IMessage evt)> Sent { get; } = [];
    public List<ScheduledCallback> Scheduled { get; } = [];
    public List<CanceledCallback> Canceled { get; } = [];
    public Action<IMessage, TopologyAudience>? OnPublish { get; set; }

    public EventEnvelope InboundEnvelope { get; }
    public string AgentId => Agent.Id;
    public IAgent Agent { get; }
    public IServiceProvider Services { get; }
    public ILogger Logger { get; }
    public string RunId => Agent is IWorkflowExecutionStateHost host ? host.RunId : Agent.Id;
    public string ScopeId => StateHost.ScopeId;
    public WorkflowCallerNyxIdAuthority? CallerNyxIdAuthority
    {
        get
        {
            var source = StateHost.ExecutionContextSnapshot.CallerCredential?.NyxIdAuthority;
            return WorkflowRunExecutionContextStateAccess.TryNormalizeCallerNyxIdAuthority(source, out var authority)
                ? authority
                : null;
        }
    }
    public WorkflowExecutionRuntimeContext RuntimeContext => Agent is IWorkflowExecutionStateHost host
        ? host.RuntimeContext
        : _fallbackRuntimeContext;

    public IWorkflowExecutionStateHost StateHost => Agent is IWorkflowExecutionStateHost host
        ? host
        : throw new InvalidOperationException("Workflow execution state host is required.");

    private readonly WorkflowExecutionRuntimeContext _fallbackRuntimeContext = new();
    private TimeSpan? _nextElapsedTime;

    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

    public long GetTimestamp() => 1;

    public TimeSpan GetElapsedTime(long startingTimestamp)
    {
        _ = startingTimestamp;
        return _nextElapsedTime ?? TimeSpan.Zero;
    }

    public void SetNextElapsedTime(TimeSpan elapsedTime)
    {
        _nextElapsedTime = elapsedTime;
    }

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new()
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            return new TState();

        var packed = host.GetExecutionState(scopeKey);
        if (packed == null || !packed.Is(new TState().Descriptor))
            return new TState();

        return packed.Unpack<TState>() ?? new TState();
    }

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new()
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            return [];

        var states = new List<KeyValuePair<string, TState>>();
        foreach (var (scopeKey, packed) in host.GetExecutionStates())
        {
            if (!string.IsNullOrEmpty(scopeKeyPrefix) &&
                !scopeKey.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!packed.Is(new TState().Descriptor))
                continue;

            states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
        }

        return states;
    }

    public Task SaveStateAsync<TState>(
        string scopeKey,
        TState state,
        CancellationToken ct = default)
        where TState : class, IMessage<TState>
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            throw new InvalidOperationException("Workflow execution state host is required.");

        return host.UpsertExecutionStateAsync(scopeKey, Any.Pack(state), ct);
    }

    public Task ClearStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            throw new InvalidOperationException("Workflow execution state host is required.");

        return host.ClearExecutionStateAsync(scopeKey, ct);
    }

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience direction = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        _ = options;
        Published.Add((evt, direction));
        OnPublish?.Invoke(evt, direction);
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        Sent.Add((targetActorId, evt));
        _ = options;
        return PublishAsync(evt, TopologyAudience.Self, ct);
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        var lease = Schedule(callbackId, evt, dueTime, period: null, options);
        return Task.FromResult(lease);
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        _ = ct;
        var lease = Schedule(callbackId, evt, dueTime, period, options);
        return Task.FromResult(lease);
    }

    public Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default)
    {
        _ = ct;
        Canceled.Add(new CanceledCallback(lease));
        return Task.CompletedTask;
    }

    public EventEnvelope CreateScheduledEnvelope(
        ScheduledCallback callback,
        string? publisherId = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(UtcNow),
            Payload = Any.Pack(callback.Event),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId ?? AgentId, TopologyAudience.Self),
        };

        if (callback.Options?.Propagation != null)
            ApplyPropagationOverrides(envelope.EnsurePropagation(), callback.Options.Propagation);

        if (!string.IsNullOrWhiteSpace(callback.Options?.Delivery?.OperationId))
            envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = callback.Options.Delivery.OperationId;

        envelope.EnsureRuntime().Callback = new EnvelopeCallbackContext
        {
            CallbackId = callback.CallbackId,
            Generation = callback.Generation,
            FireIndex = 0,
            FiredAtUnixTimeMs = UtcNow.ToUnixTimeMilliseconds(),
        };
        return envelope;
    }

    private RuntimeCallbackLease Schedule(
        string callbackId,
        IMessage evt,
        TimeSpan dueTime,
        TimeSpan? period,
        EventEnvelopePublishOptions? options)
    {
        var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
        _generations[callbackId] = generation;

        Scheduled.Add(new ScheduledCallback(
            callbackId,
            generation,
            evt,
            dueTime,
            period,
            CloneOptions(options)));

        return new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory);
    }

    private static EventEnvelopePublishOptions? CloneOptions(EventEnvelopePublishOptions? options)
    {
        if (options == null)
            return null;

        return options.DeepClone();
    }

    private static void ApplyPropagationOverrides(
        EnvelopePropagation target,
        EventEnvelopePropagationOverrides overrides)
    {
        if (!string.IsNullOrWhiteSpace(overrides.CorrelationId))
            target.CorrelationId = overrides.CorrelationId;
        if (!string.IsNullOrWhiteSpace(overrides.CausationEventId))
            target.CausationEventId = overrides.CausationEventId;
        if (overrides.Trace != null)
            target.Trace = overrides.Trace.Clone();

        foreach (var pair in overrides.Baggage)
            target.Baggage[pair.Key] = pair.Value;
    }
}

internal sealed record ScheduledCallback(
    string CallbackId,
    long Generation,
    IMessage Event,
    TimeSpan DueTime,
    TimeSpan? Period,
    EventEnvelopePublishOptions? Options);

internal sealed record CanceledCallback(
    RuntimeCallbackLease Lease)
{
    public string CallbackId => Lease.CallbackId;

    public long ExpectedGeneration => Lease.Generation;
}

internal sealed class TestAgent(
    string id,
    string? runId = null,
    string? scopeId = null,
    IRuntimeSecretStore? runtimeSecretStore = null) :
    IAgent,
    IWorkflowExecutionStateHost,
    IRuntimeSecretStoreAccessor
{
    private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);
    private readonly IRuntimeSecretStore _runtimeSecretStore = runtimeSecretStore ?? new InMemoryRuntimeSecretStore();

    public string Id { get; } = id;

    public string RunId { get; } = string.IsNullOrWhiteSpace(runId) ? id : runId;

    public string ScopeId { get; } = string.IsNullOrWhiteSpace(scopeId) ? string.Empty : scopeId.Trim();

    public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

    public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

    public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

    public IRuntimeSecretStore RuntimeSecretStore => _runtimeSecretStore;

    public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
    {
        WorkflowExecutionContextTestState.ApplyDelta(ExecutionContextState, delta);
        return Task.CompletedTask;
    }

    public Task ClearExecutionContextAsync(CancellationToken ct = default)
    {
        ExecutionContextState.Llm = null;
        ExecutionContextState.CallerCredential = null;
        return Task.CompletedTask;
    }

    public Any? GetExecutionState(string scopeKey) =>
        _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        _executionStates.ToList();

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates[scopeKey] = state;
        return Task.CompletedTask;
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates.Remove(scopeKey);
        return Task.CompletedTask;
    }

    Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
        WorkflowCompletedEvent terminalFailure,
        StepCompletedEvent? terminalStep,
        CancellationToken ct)
    {
        _ = terminalFailure;
        _ = terminalStep;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
        CompensableStepDispatchedEvent evt,
        CancellationToken ct)
    {
        _ = evt;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct = default)
    {
        _ = completion;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
        string runId,
        string error,
        CancellationToken ct = default)
    {
        _ = runId;
        _ = error;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
        new(
            WorkflowCompensationTransitionStatus.NoCompensableLedger,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestWorkflowRunAgent(string id, string runId) :
    IAgent,
    IWorkflowExecutionStateHost,
    IRuntimeSecretStoreAccessor
{
    private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);
    private readonly IRuntimeSecretStore _runtimeSecretStore = new InMemoryRuntimeSecretStore();

    public string Id { get; } = id;

    public string RunId { get; } = runId;

    public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

    public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

    public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

    public IRuntimeSecretStore RuntimeSecretStore => _runtimeSecretStore;

    public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
    {
        WorkflowExecutionContextTestState.ApplyDelta(ExecutionContextState, delta);
        return Task.CompletedTask;
    }

    public Task ClearExecutionContextAsync(CancellationToken ct = default)
    {
        ExecutionContextState.Llm = null;
        ExecutionContextState.CallerCredential = null;
        return Task.CompletedTask;
    }

    public Any? GetExecutionState(string scopeKey) =>
        _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        _executionStates.ToList();

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates[scopeKey] = state;
        return Task.CompletedTask;
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates.Remove(scopeKey);
        return Task.CompletedTask;
    }

    Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
        WorkflowCompletedEvent terminalFailure,
        StepCompletedEvent? terminalStep,
        CancellationToken ct)
    {
        _ = terminalFailure;
        _ = terminalStep;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
        CompensableStepDispatchedEvent evt,
        CancellationToken ct)
    {
        _ = evt;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct = default)
    {
        _ = completion;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
        string runId,
        string error,
        CancellationToken ct = default)
    {
        _ = runId;
        _ = error;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoCompensableLedger());
    }

    private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
        new(
            WorkflowCompensationTransitionStatus.NoCompensableLedger,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("stub-workflow-run");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal static class WorkflowExecutionContextTestState
{
    public static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
                RuntimeSecretReference = delta.CallerCredential.RuntimeSecretReference?.Clone(),
                DurableCallerCredential = delta.CallerCredential.DurableCallerCredential?.Clone(),
                NyxIdAuthority = delta.CallerCredential.NyxIdAuthority?.Clone(),
            };
        }
    }
}
