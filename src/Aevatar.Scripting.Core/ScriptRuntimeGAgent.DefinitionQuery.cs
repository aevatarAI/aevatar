using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent
{
    [EventHandler]
    public async Task HandleScriptDefinitionSnapshotResponded(ScriptDefinitionSnapshotRespondedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        Logger.LogInformation(
            "Script definition query response received. runtime_actor_id={RuntimeActorId} request_id={RequestId} found={Found} revision={Revision}",
            Id,
            evt.RequestId,
            evt.Found,
            evt.Revision);

        if (!TryGetPendingRunContext(evt.RequestId, out var pending))
        {
            Logger.LogDebug(
                "Ignoring unmatched script definition snapshot response. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                evt.RequestId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.Pending.RunEvent.RunId) &&
            string.Equals(State.LastRunId, pending.Pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            await PersistPendingDefinitionQueryClearedAsync(
                pending.Pending.RequestId,
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "already_committed",
                CancellationToken.None);
            Logger.LogDebug(
                "Script definition query response ignored because run already committed. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.Pending.RunEvent.RunId,
                pending.Pending.RequestId);
            return;
        }

        if (!evt.Found)
        {
            await PersistPendingRunFailureAsync(
                pending.Pending,
                $"Definition snapshot query returned not found. request_id={pending.Pending.RequestId} reason={evt.FailureReason}",
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "not_found",
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition snapshot query returned not found. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} reason={Reason}",
                Id,
                pending.Pending.RunEvent.RunId,
                evt.RequestId,
                evt.FailureReason);
            return;
        }

        var snapshot = new ScriptDefinitionSnapshot(
            evt.ScriptId ?? string.Empty,
            evt.Revision ?? string.Empty,
            evt.SourceText ?? string.Empty,
            evt.ReadModelSchemaVersion ?? string.Empty,
            evt.ReadModelSchemaHash ?? string.Empty);

        if (string.IsNullOrWhiteSpace(snapshot.SourceText))
        {
            await PersistPendingRunFailureAsync(
                pending.Pending,
                $"Definition snapshot query returned empty source. request_id={pending.Pending.RequestId}",
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "empty_source",
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition query returned empty source. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.Pending.RunEvent.RunId,
                evt.RequestId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.Pending.RunEvent.ScriptRevision) &&
            !string.Equals(pending.Pending.RunEvent.ScriptRevision, snapshot.Revision, StringComparison.Ordinal))
        {
            await PersistPendingRunFailureAsync(
                pending.Pending,
                "Definition snapshot revision mismatch. request_id=" + pending.Pending.RequestId +
                " requested_revision=" + pending.Pending.RunEvent.ScriptRevision +
                " actual_revision=" + snapshot.Revision,
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "revision_mismatch",
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition revision mismatch. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} requested_revision={RequestedRevision} actual_revision={ActualRevision}",
                Id,
                pending.Pending.RunEvent.RunId,
                evt.RequestId,
                pending.Pending.RunEvent.ScriptRevision,
                snapshot.Revision);
            return;
        }

        try
        {
            await PersistRunCommittedAsync(
                pending.Pending,
                snapshot,
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "completed",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistPendingRunFailureAsync(
                pending.Pending,
                $"Script run execution failed after definition query. request_id={pending.Pending.RequestId} reason={ex.Message}",
                CancellationToken.None);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "execution_failed",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Script run failed after definition query. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.Pending.RunEvent.RunId,
                pending.Pending.RequestId);
        }
    }

    [AllEventHandler(Priority = 0, AllowSelfHandling = true)]
    public async Task HandleRuntimeCallbackEnvelope(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload?.Is(ScriptDefinitionQueryTimeoutFiredEvent.Descriptor) != true)
            return;

        var evt = envelope.Payload.Unpack<ScriptDefinitionQueryTimeoutFiredEvent>();
        if (!TryGetPendingRunContext(evt.RequestId, out var pending))
        {
            Logger.LogDebug(
                "Ignoring stale script definition query timeout signal. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                evt.RequestId);
            return;
        }

        if (pending.TimeoutLease == null ||
            !RuntimeCallbackEnvelopeMetadataReader.MatchesLease(envelope, pending.TimeoutLease))
        {
            Logger.LogDebug(
                "Ignoring script definition query timeout without matching lease metadata. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                evt.RequestId);
            return;
        }

        await HandleScriptDefinitionQueryTimeoutFiredAsync(evt, pending, CancellationToken.None);
    }

    private async Task RecoverPendingDefinitionQueryAsync(
        PendingScriptDefinitionQueryState pending,
        CancellationToken ct)
    {
        PendingRunRuntimeContext runtimePending;
        try
        {
            runtimePending = await ArmPendingDefinitionQueryAsync(pending, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to recover script definition query timeout. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                pending.RequestId);
            return;
        }

        try
        {
            await DispatchDefinitionQueryAsync(pending, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to redispatch recovered script definition query. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                pending.RequestId);
        }
    }

    private async Task<PendingRunRuntimeContext> ArmPendingDefinitionQueryAsync(
        PendingScriptDefinitionQueryState pending,
        CancellationToken ct)
    {
        var timeoutLease = await ScheduleSelfDurableTimeoutAsync(
            pending.TimeoutCallbackId,
            PendingRunTimeout,
            new ScriptDefinitionQueryTimeoutFiredEvent
            {
                RequestId = pending.RequestId,
                RunId = pending.RunEvent.RunId ?? string.Empty,
            },
            ct: ct);

        var runtimePending = new PendingRunRuntimeContext(pending.Clone(), timeoutLease);
        _pendingDefinitionQueryLeases[pending.RequestId] = runtimePending;
        return runtimePending;
    }

    private async Task DispatchDefinitionQueryAsync(
        PendingScriptDefinitionQueryState pending,
        CancellationToken ct)
    {
        await SendToAsync(
            pending.RunEvent.DefinitionActorId,
            new QueryScriptDefinitionSnapshotRequestedEvent
            {
                RequestId = pending.RequestId,
                ReplyStreamId = Id,
                RequestedRevision = pending.RunEvent.ScriptRevision ?? string.Empty,
            },
            ct);
    }

    private async Task HandleScriptDefinitionQueryTimeoutFiredAsync(
        ScriptDefinitionQueryTimeoutFiredEvent evt,
        PendingRunRuntimeContext pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!string.IsNullOrWhiteSpace(evt.RunId) &&
            !string.Equals(evt.RunId, pending.Pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignoring mismatched script definition query timeout signal. runtime_actor_id={RuntimeActorId} request_id={RequestId} expected_run_id={ExpectedRunId} signal_run_id={SignalRunId}",
                Id,
                evt.RequestId,
                pending.Pending.RunEvent.RunId,
                evt.RunId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.Pending.RunEvent.RunId) &&
            string.Equals(State.LastRunId, pending.Pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            await PersistPendingDefinitionQueryClearedAsync(
                pending.Pending.RequestId,
                ct);
            await ClearPendingDefinitionQueryRuntimeAsync(
                pending,
                "already_committed",
                ct);
            Logger.LogDebug(
                "Script definition query timeout signal ignored because run already committed. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.Pending.RunEvent.RunId,
                pending.Pending.RequestId);
            return;
        }

        await PersistPendingRunFailureAsync(
            pending.Pending,
            $"Definition snapshot query timed out. request_id={pending.Pending.RequestId} timeout_seconds={PendingRunTimeout.TotalSeconds}",
            ct);
        await ClearPendingDefinitionQueryRuntimeAsync(
            pending,
            "timed_out",
            CancellationToken.None);
        Logger.LogWarning(
            "Script definition query timed out. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
            Id,
            pending.Pending.RunEvent.RunId,
            pending.Pending.RequestId);
    }
}
