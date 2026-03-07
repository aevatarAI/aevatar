using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Scripting.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent
{
    [EventHandler]
    public async Task HandleRunScriptRequested(RunScriptRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            throw new InvalidOperationException("DefinitionActorId is required.");

        Logger.LogInformation(
            "Script run requested. runtime_actor_id={RuntimeActorId} run_id={RunId} correlation_id={CorrelationId} definition_actor_id={DefinitionActorId} revision={Revision}",
            Id,
            evt.RunId,
            evt.RunId,
            evt.DefinitionActorId,
            evt.ScriptRevision);

        if (_snapshotPort.UseEventDrivenDefinitionQuery)
        {
            await QueueRunByDefinitionQueryAsync(evt, CancellationToken.None);
            return;
        }

        try
        {
            var snapshot = await LoadDefinitionSnapshotAsync(
                evt.DefinitionActorId,
                evt.ScriptRevision,
                CancellationToken.None);
            await PersistRunCommittedAsync(
                evt,
                snapshot,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistRunFailureAsync(
                evt,
                $"Failed to execute script run without query pipeline: {ex.Message}",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Script run failed before commit. runtime_actor_id={RuntimeActorId} run_id={RunId} definition_actor_id={DefinitionActorId}",
                Id,
                evt.RunId,
                evt.DefinitionActorId);
        }
    }

    private async Task QueueRunByDefinitionQueryAsync(RunScriptRequestedEvent evt, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var runEvent = evt.Clone();
        var queuedAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timeoutCallbackId = BuildDefinitionQueryTimeoutCallbackId(requestId);

        await PersistDomainEventAsync(
            new ScriptDefinitionQueryQueuedEvent
            {
                RequestId = requestId,
                RunEvent = runEvent,
                QueuedAtUnixTimeMs = queuedAtUnixTimeMs,
                TimeoutCallbackId = timeoutCallbackId,
            },
            ct);

        var pendingState = GetRequiredPendingState(requestId);
        PendingRunRuntimeContext pending;
        try
        {
            pending = await ArmPendingDefinitionQueryAsync(pendingState, ct);
        }
        catch (Exception ex)
        {
            await PersistPendingRunFailureAsync(
                pendingState,
                $"Failed to schedule definition query timeout. request_id={requestId} reason={ex.Message}",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Failed to schedule definition query timeout. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                evt.RunId,
                requestId);
            return;
        }

        try
        {
            await DispatchDefinitionQueryAsync(pendingState, ct);
        }
        catch (Exception ex)
        {
            await PersistPendingRunFailureAsync(
                pendingState,
                $"Failed to dispatch definition query. request_id={requestId} reason={ex.Message}",
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "dispatch_failed",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Failed to dispatch definition query. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                evt.RunId,
                requestId);
            return;
        }

        Logger.LogInformation(
            "Script definition query dispatched. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} definition_actor_id={DefinitionActorId} revision={Revision}",
            Id,
            evt.RunId,
            requestId,
            evt.DefinitionActorId,
            evt.ScriptRevision);
    }
}
