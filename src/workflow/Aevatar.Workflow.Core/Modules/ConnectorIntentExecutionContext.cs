using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

internal sealed class ConnectorIntentExecutionContext : IWorkflowExecutionContext
{
    private readonly ConnectorCallIntentEvent _intent;

    // Refactor (iter110/cluster-1): Old pattern: connector resolver depended on the live module context while IO happened inline.  New principle: executor resolves connector from a narrow intent-backed context without actor-turn state mutation.
    public ConnectorIntentExecutionContext(ConnectorCallIntentEvent intent)
    {
        _intent = intent ?? throw new ArgumentNullException(nameof(intent));
    }

    public EventEnvelope InboundEnvelope { get; } = new();
    public string AgentId => string.Empty;
    public string RunId => _intent.RunId;
    public IServiceProvider Services => EmptyServiceProvider.Instance;
    public ILogger Logger => NullLogger.Instance;
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
    public long GetTimestamp() => TimeProvider.System.GetTimestamp();
    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeProvider.System.GetElapsedTime(startingTimestamp);

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new() =>
        new();

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new() =>
        [];

    public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
        where TState : class, IMessage<TState> =>
        Task.CompletedTask;

    public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience direction = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage =>
        Task.CompletedTask;

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage =>
        Task.CompletedTask;

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(new RuntimeCallbackLease(string.Empty, callbackId, 0, RuntimeCallbackBackend.InMemory));

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(new RuntimeCallbackLease(string.Empty, callbackId, 0, RuntimeCallbackBackend.InMemory));

    public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
        Task.CompletedTask;

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
