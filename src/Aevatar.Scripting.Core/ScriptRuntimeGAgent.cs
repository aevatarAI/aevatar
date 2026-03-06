using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    private const string RunFailedEventType = "script.run.failed";
    private const string DefinitionQueryTimeoutCallbackPrefix = "script-definition-query-timeout";
    private static readonly TimeSpan PendingRunTimeout = TimeSpan.FromSeconds(45);

    private readonly IScriptRuntimeExecutionOrchestrator _orchestrator;
    private readonly IScriptDefinitionSnapshotPort _snapshotPort;
    private readonly Dictionary<string, PendingRunRuntimeContext> _pendingDefinitionQueries = new(StringComparer.Ordinal);

    public ScriptRuntimeGAgent(
        IScriptRuntimeExecutionOrchestrator orchestrator,
        IScriptDefinitionSnapshotPort snapshotPort)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _snapshotPort = snapshotPort ?? throw new ArgumentNullException(nameof(snapshotPort));
        InitializeId();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        _pendingDefinitionQueries.Clear();

        foreach (var pending in State.PendingDefinitionQueries.Values.OrderBy(x => x.QueuedAtUnixTimeMs))
            await RecoverPendingDefinitionQueryAsync(pending, ct);
    }

    protected override Task OnDeactivateAsync(CancellationToken ct)
    {
        _pendingDefinitionQueries.Clear();
        return Task.CompletedTask;
    }

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
        _pendingDefinitionQueries[pending.RequestId] = runtimePending;
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

    private async Task PersistRunCommittedAsync(
        RunScriptRequestedEvent runEvent,
        ScriptDefinitionSnapshot snapshot,
        CancellationToken ct)
    {
        var committedEvents = await _orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: Id,
                CurrentState: ClonePayloads(State.StatePayloads),
                CurrentReadModel: ClonePayloads(State.ReadModelPayloads),
                RunEvent: runEvent,
                ScriptId: snapshot.ScriptId,
                ScriptRevision: snapshot.Revision,
                SourceText: snapshot.SourceText,
                ReadModelSchemaVersion: snapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash: snapshot.ReadModelSchemaHash),
            ct);
        await PersistDomainEventsAsync(committedEvents, ct);

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            runEvent.RunId,
            committedEvents.Count,
            snapshot.Revision);
    }

    private async Task PersistRunCommittedAsync(
        PendingScriptDefinitionQueryState pending,
        ScriptDefinitionSnapshot snapshot,
        CancellationToken ct)
    {
        var committedEvents = await _orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: Id,
                CurrentState: ClonePayloads(State.StatePayloads),
                CurrentReadModel: ClonePayloads(State.ReadModelPayloads),
                RunEvent: pending.RunEvent,
                ScriptId: snapshot.ScriptId,
                ScriptRevision: snapshot.Revision,
                SourceText: snapshot.SourceText,
                ReadModelSchemaVersion: snapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash: snapshot.ReadModelSchemaHash),
            ct);
        await PersistDomainEventsAsync(
            committedEvents.Concat<IMessage>([BuildDefinitionQueryClearedEvent(pending.RequestId)]),
            ct);

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            pending.RunEvent.RunId,
            committedEvents.Count,
            snapshot.Revision);
    }

    private static string BuildDefinitionQueryTimeoutCallbackId(string requestId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            DefinitionQueryTimeoutCallbackPrefix,
            requestId);

    private PendingScriptDefinitionQueryState GetRequiredPendingState(string requestId)
    {
        if (State.PendingDefinitionQueries.TryGetValue(requestId, out var pending))
            return pending.Clone();

        throw new InvalidOperationException($"Missing pending script definition query state for request_id={requestId}.");
    }

    private bool TryGetPendingRunContext(string requestId, out PendingRunRuntimeContext pending)
    {
        pending = new PendingRunRuntimeContext(
            new PendingScriptDefinitionQueryState(),
            null);
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        if (_pendingDefinitionQueries.TryGetValue(requestId, out var runtimePending))
        {
            pending = runtimePending;
            return true;
        }

        if (!State.PendingDefinitionQueries.TryGetValue(requestId, out var persisted))
            return false;

        pending = new PendingRunRuntimeContext(persisted.Clone(), null);
        return true;
    }

    private async Task ClearPendingDefinitionQueryRuntimeAsync(
        PendingRunRuntimeContext pending,
        string reason,
        CancellationToken ct)
    {
        if (_pendingDefinitionQueries.Remove(pending.Pending.RequestId, out var runtimePending))
            pending = runtimePending;

        if (pending.TimeoutLease == null)
            return;

        try
        {
            await CancelDurableCallbackAsync(pending.TimeoutLease, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to cancel script definition query timeout. runtime_actor_id={RuntimeActorId} request_id={RequestId} reason={Reason}",
                Id,
                pending.Pending.RequestId,
                reason);
        }
    }

    private async Task PersistRunFailureAsync(
        RunScriptRequestedEvent runEvent,
        string reason,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(
            BuildRunFailureCommittedEvent(runEvent, reason),
            ct);
    }

    private async Task PersistPendingRunFailureAsync(
        PendingScriptDefinitionQueryState pending,
        string reason,
        CancellationToken ct)
    {
        await PersistDomainEventsAsync(
            [
                BuildRunFailureCommittedEvent(pending.RunEvent, reason),
                BuildDefinitionQueryClearedEvent(pending.RequestId),
            ],
            ct);
    }

    private async Task PersistPendingDefinitionQueryClearedAsync(
        string requestId,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(
            BuildDefinitionQueryClearedEvent(requestId),
            ct);
    }

    private static ScriptDefinitionQueryClearedEvent BuildDefinitionQueryClearedEvent(string requestId) =>
        new()
        {
            RequestId = requestId ?? string.Empty,
        };

    private ScriptRunDomainEventCommitted BuildRunFailureCommittedEvent(
        RunScriptRequestedEvent runEvent,
        string reason)
    {
        var committed = new ScriptRunDomainEventCommitted
        {
            RunId = runEvent.RunId ?? string.Empty,
            ScriptRevision = string.IsNullOrWhiteSpace(runEvent.ScriptRevision) ? State.Revision : runEvent.ScriptRevision,
            DefinitionActorId = runEvent.DefinitionActorId ?? string.Empty,
            EventType = RunFailedEventType,
            Payload = Any.Pack(new StringValue
            {
                Value = string.IsNullOrWhiteSpace(reason) ? "Script run failed." : reason,
            }),
            ReadModelSchemaVersion = State.LastAppliedSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.LastSchemaHash ?? string.Empty,
        };
        CopyPayloads(State.StatePayloads, committed.StatePayloads);
        CopyPayloads(State.ReadModelPayloads, committed.ReadModelPayloads);
        return committed;
    }

    protected override ScriptRuntimeState TransitionState(ScriptRuntimeState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDefinitionQueryQueuedEvent>(ApplyQueued)
            .On<ScriptDefinitionQueryClearedEvent>(ApplyCleared)
            .On<ScriptRunDomainEventCommitted>(ApplyCommitted)
            .OrCurrent();

    private static ScriptRuntimeState ApplyQueued(
        ScriptRuntimeState state,
        ScriptDefinitionQueryQueuedEvent queued)
    {
        var next = state.Clone();
        next.PendingDefinitionQueries[queued.RequestId] = new PendingScriptDefinitionQueryState
        {
            RequestId = queued.RequestId ?? string.Empty,
            RunEvent = queued.RunEvent?.Clone() ?? new RunScriptRequestedEvent(),
            QueuedAtUnixTimeMs = queued.QueuedAtUnixTimeMs,
            TimeoutCallbackId = queued.TimeoutCallbackId ?? string.Empty,
        };
        return next;
    }

    private static ScriptRuntimeState ApplyCleared(
        ScriptRuntimeState state,
        ScriptDefinitionQueryClearedEvent cleared)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(cleared.RequestId))
            next.PendingDefinitionQueries.Remove(cleared.RequestId);
        return next;
    }

    private static ScriptRuntimeState ApplyCommitted(
        ScriptRuntimeState state,
        ScriptRunDomainEventCommitted committed)
    {
        var next = state.Clone();
        if (!string.Equals(committed.EventType, RunFailedEventType, StringComparison.Ordinal))
        {
            next.DefinitionActorId = committed.DefinitionActorId ?? string.Empty;
            next.Revision = committed.ScriptRevision ?? string.Empty;
        }

        next.LastRunId = committed.RunId ?? string.Empty;
        CopyPayloads(committed.StatePayloads, next.StatePayloads);
        CopyPayloads(committed.ReadModelPayloads, next.ReadModelPayloads);
        next.LastAppliedSchemaVersion = committed.ReadModelSchemaVersion ?? string.Empty;
        next.LastSchemaHash = committed.ReadModelSchemaHash ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = committed.RunId ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(committed.RunId))
        {
            var pendingKeys = next.PendingDefinitionQueries
                .Where(x => string.Equals(x.Value.RunEvent?.RunId, committed.RunId, StringComparison.Ordinal))
                .Select(x => x.Key)
                .ToArray();
            foreach (var pendingKey in pendingKeys)
                next.PendingDefinitionQueries.Remove(pendingKey);
        }

        return next;
    }

    private async Task<ScriptDefinitionSnapshot> LoadDefinitionSnapshotAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        return await _snapshotPort.GetRequiredAsync(definitionActorId, requestedRevision, ct);
    }

    private static IReadOnlyDictionary<string, Any> ClonePayloads(
        MapField<string, Any> payloads)
    {
        if (payloads.Count == 0)
            return new Dictionary<string, Any>(StringComparer.Ordinal);

        var clone = new Dictionary<string, Any>(
            payloads.Count,
            StringComparer.Ordinal);
        foreach (var (key, value) in payloads)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            clone[key] = value.Clone();
        }

        return clone;
    }

    private static void CopyPayloads(
        MapField<string, Any> source,
        MapField<string, Any> target)
    {
        target.Clear();
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            target[key] = value.Clone();
        }
    }

    private sealed record PendingRunRuntimeContext(
        PendingScriptDefinitionQueryState Pending,
        RuntimeCallbackLease? TimeoutLease);
}
