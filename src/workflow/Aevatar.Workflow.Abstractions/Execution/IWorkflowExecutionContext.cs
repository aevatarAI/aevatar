using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Abstractions.Execution;

public interface IWorkflowExecutionContext
    : IEventContext
{
    string RunId { get; }

    TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new();

    IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new();

    Task SaveStateAsync<TState>(
        string scopeKey,
        TState state,
        CancellationToken ct = default)
        where TState : class, IMessage<TState>;

    Task ClearStateAsync(
        string scopeKey,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default);

    Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default);
}
