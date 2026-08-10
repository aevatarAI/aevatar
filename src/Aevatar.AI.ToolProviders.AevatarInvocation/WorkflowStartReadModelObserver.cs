using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal sealed class WorkflowStartReadModelObserver
{
    internal static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeProvider _timeProvider;

    public WorkflowStartReadModelObserver(
        IWorkflowExecutionQueryApplicationService queryService,
        TimeSpan? observationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _observationTimeout = observationTimeout ?? DefaultObservationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> ObserveAsync(
        string scopeId,
        string actorId,
        string commandId,
        CancellationToken ct)
    {
        if (!_queryService.WorkflowActorCurrentStateQueryEnabled ||
            string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        var normalizedScopeId = scopeId.Trim();
        var normalizedActorId = actorId.Trim();
        var normalizedCommandId = commandId.Trim();
        var deadline = _timeProvider.GetUtcNow() + _observationTimeout;

        while (true)
        {
            try
            {
                var snapshot = await _queryService.GetWorkflowActorCurrentStateAsync(normalizedActorId, ct)
                    .ConfigureAwait(false);
                if (snapshot is { StateVersion: > 0 } &&
                    string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.ActorId, normalizedActorId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.LastCommandId, normalizedCommandId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return false;

            await Task.Delay(
                    remaining < ObservationInterval ? remaining : ObservationInterval,
                    _timeProvider,
                    ct)
                .ConfigureAwait(false);
        }
    }
}
