using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Abstractions.Execution;

public interface IWorkflowExecutionContext
    : IEventContext
{
    string RunId { get; }

    string ScopeId => string.Empty;

    string ScheduleId => string.Empty;

    string ToolCatalogPolicyVersion => WorkflowToolCatalogPolicies.LegacyV0;

    WorkflowCallerNyxIdAuthority? CallerNyxIdAuthority => null;

    // Refactor (iter89/cluster-089-workflow-module-clock-state):
    //   Old: Workflow modules read process wall clock directly for TTLs,
    //        timeout stamps, buffered signal eviction, and elapsed metrics.
    //   New: Workflow modules consume injected execution context time for
    //        business timestamps and monotonic elapsed APIs for durations.
    DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();

    long GetTimestamp() => TimeProvider.System.GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp) =>
        TimeProvider.System.GetElapsedTime(startingTimestamp);

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

    Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default);
}
