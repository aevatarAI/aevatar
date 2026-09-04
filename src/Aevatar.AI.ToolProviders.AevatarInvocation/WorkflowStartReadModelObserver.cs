using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal sealed class WorkflowStartReadModelObserver
{
    internal static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan DefaultCompletionObservationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeSpan _completionObservationTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowStartReadModelObserver> _logger;

    public WorkflowStartReadModelObserver(
        IWorkflowExecutionQueryApplicationService queryService,
        TimeSpan? observationTimeout = null,
        TimeSpan? completionObservationTimeout = null,
        TimeProvider? timeProvider = null,
        ILogger<WorkflowStartReadModelObserver>? logger = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _observationTimeout = observationTimeout ?? DefaultObservationTimeout;
        _completionObservationTimeout = completionObservationTimeout ?? DefaultCompletionObservationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WorkflowStartReadModelObserver>.Instance;
    }

    public async Task<bool> ObserveAsync(
        string scopeId,
        string actorId,
        string commandId,
        CancellationToken ct) =>
        await ObserveSnapshotAsync(scopeId, actorId, commandId, ct).ConfigureAwait(false) != null;

    public async Task<WorkflowActorSnapshot?> ObserveSnapshotAsync(
        string scopeId,
        string actorId,
        string commandId,
        CancellationToken ct) =>
        await ObserveMatchingSnapshotAsync(
                scopeId,
                actorId,
                commandId,
                static _ => true,
                _observationTimeout,
                initialObservationTimeout: null,
                ct)
            .ConfigureAwait(false);

    public async Task<WorkflowActorSnapshot?> ObserveCompletionAsync(
        string scopeId,
        string actorId,
        string commandId,
        CancellationToken ct) =>
        await ObserveMatchingSnapshotAsync(
                scopeId,
                actorId,
                commandId,
                static snapshot => IsTerminal(snapshot.CompletionStatus),
                _completionObservationTimeout,
                initialObservationTimeout: null,
                ct)
            .ConfigureAwait(false);

    public async Task<WorkflowActorSnapshot?> ObserveInteractivePauseAsync(
        string scopeId,
        string actorId,
        string commandId,
        TimeSpan observationTimeout,
        CancellationToken ct) =>
        await ObserveInteractivePauseAsync(
                scopeId,
                actorId,
                commandId,
                observationTimeout,
                initialObservationTimeout: null,
                ct)
            .ConfigureAwait(false);

    public async Task<WorkflowActorSnapshot?> ObserveInteractivePauseAsync(
        string scopeId,
        string actorId,
        string commandId,
        TimeSpan observationTimeout,
        TimeSpan? initialObservationTimeout,
        CancellationToken ct) =>
        await ObserveMatchingSnapshotAsync(
                scopeId,
                actorId,
                commandId,
                static snapshot => IsTerminal(snapshot.CompletionStatus) ||
                                   snapshot.CompletionStatus == WorkflowRunCompletionStatus.WaitingForSignal,
                observationTimeout,
                initialObservationTimeout,
                ct)
            .ConfigureAwait(false);

    private async Task<WorkflowActorSnapshot?> ObserveMatchingSnapshotAsync(
        string scopeId,
        string actorId,
        string commandId,
        Func<WorkflowActorSnapshot, bool> accept,
        TimeSpan observationTimeout,
        TimeSpan? initialObservationTimeout,
        CancellationToken ct)
    {
        if (!_queryService.WorkflowActorCurrentStateQueryEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var normalizedScopeId = scopeId.Trim();
        var normalizedActorId = actorId.Trim();
        var normalizedCommandId = commandId.Trim();
        var deadline = _timeProvider.GetUtcNow() + observationTimeout;
        var initialDeadline = initialObservationTimeout.HasValue
            ? _timeProvider.GetUtcNow() + initialObservationTimeout.Value
            : (DateTimeOffset?)null;
        var matchingSnapshotObserved = false;

        while (true)
        {
            try
            {
                var snapshot = await _queryService.GetWorkflowActorCurrentStateAsync(normalizedActorId, ct)
                    .ConfigureAwait(false);
                if (IsMatchingSnapshotIdentity(snapshot, normalizedScopeId, normalizedActorId, normalizedCommandId))
                {
                    matchingSnapshotObserved = true;
                    if (accept(snapshot!))
                    {
                        return snapshot;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Workflow start read model observation failed for actor {ActorId} command {CommandId}.",
                    normalizedActorId,
                    normalizedCommandId);
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            var effectiveDeadline = !matchingSnapshotObserved && initialDeadline.HasValue && initialDeadline.Value < deadline
                ? initialDeadline.Value
                : deadline;
            var remaining = effectiveDeadline - now;
            if (remaining <= TimeSpan.Zero)
                return null;

            await Task.Delay(
                    remaining < ObservationInterval ? remaining : ObservationInterval,
                    _timeProvider,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static bool IsMatchingSnapshotIdentity(
        WorkflowActorSnapshot? snapshot,
        string normalizedScopeId,
        string normalizedActorId,
        string normalizedCommandId) =>
        snapshot is { StateVersion: > 0 } &&
        (string.IsNullOrWhiteSpace(normalizedScopeId) ||
         string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal)) &&
        string.Equals(snapshot.ActorId, normalizedActorId, StringComparison.Ordinal) &&
        string.Equals(snapshot.LastCommandId, normalizedCommandId, StringComparison.Ordinal);

    private static bool IsTerminal(WorkflowRunCompletionStatus status) =>
        status is WorkflowRunCompletionStatus.Completed or
            WorkflowRunCompletionStatus.Failed or
            WorkflowRunCompletionStatus.Stopped or
            WorkflowRunCompletionStatus.TimedOut or
            WorkflowRunCompletionStatus.NotFound or
            WorkflowRunCompletionStatus.Disabled or
            WorkflowRunCompletionStatus.Unknown;
}
