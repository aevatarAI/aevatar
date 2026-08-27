using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal sealed class WorkflowStartReadModelObserver
{
    internal static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan DefaultCompletionObservationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeSpan _completionObservationTimeout;
    private readonly TimeProvider _timeProvider;

    public WorkflowStartReadModelObserver(
        IWorkflowExecutionQueryApplicationService queryService,
        TimeSpan? observationTimeout = null,
        TimeSpan? completionObservationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _observationTimeout = observationTimeout ?? DefaultObservationTimeout;
        _completionObservationTimeout = completionObservationTimeout ?? DefaultCompletionObservationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> ObserveAsync(
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
                ct)
            .ConfigureAwait(false) != null;

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
                ct)
            .ConfigureAwait(false);

    private async Task<WorkflowActorSnapshot?> ObserveMatchingSnapshotAsync(
        string scopeId,
        string actorId,
        string commandId,
        Func<WorkflowActorSnapshot, bool> accept,
        TimeSpan observationTimeout,
        CancellationToken ct)
    {
        if (!_queryService.WorkflowActorCurrentStateQueryEnabled ||
            string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var normalizedScopeId = scopeId.Trim();
        var normalizedActorId = actorId.Trim();
        var normalizedCommandId = commandId.Trim();
        var deadline = _timeProvider.GetUtcNow() + observationTimeout;

        while (true)
        {
            try
            {
                var snapshot = await _queryService.GetWorkflowActorCurrentStateAsync(normalizedActorId, ct)
                    .ConfigureAwait(false);
                if (snapshot is { StateVersion: > 0 } &&
                    string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.ActorId, normalizedActorId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.LastCommandId, normalizedCommandId, StringComparison.Ordinal) &&
                    accept(snapshot))
                {
                    return snapshot;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return null;

            await Task.Delay(
                    remaining < ObservationInterval ? remaining : ObservationInterval,
                    _timeProvider,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(WorkflowRunCompletionStatus status) =>
        status is WorkflowRunCompletionStatus.Completed or
            WorkflowRunCompletionStatus.Failed or
            WorkflowRunCompletionStatus.Stopped or
            WorkflowRunCompletionStatus.TimedOut or
            WorkflowRunCompletionStatus.NotFound or
            WorkflowRunCompletionStatus.Disabled or
            WorkflowRunCompletionStatus.Unknown;
}
