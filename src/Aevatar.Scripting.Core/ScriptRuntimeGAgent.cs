using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    private const string RunFailedEventType = "script.run.failed";
    private static readonly TimeSpan PendingRunTimeout = TimeSpan.FromSeconds(45);
    private readonly IScriptRuntimeExecutionOrchestrator _orchestrator;
    private readonly IScriptDefinitionSnapshotPort _snapshotPort;

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
        foreach (var (requestId, pending) in State.PendingDefinitionQueries)
        {
            if (string.IsNullOrWhiteSpace(requestId) || pending?.RunEvent == null)
                continue;

            await ScheduleDefinitionQueryTimeoutAsync(
                requestId,
                pending.RunEvent.RunId ?? string.Empty,
                pending.QueuedAtUnixTimeMs,
                ct);
        }
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
                completedQueryRequestId: null,
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

        if (!string.IsNullOrWhiteSpace(pending.RunEvent.RunId) &&
            string.Equals(State.LastRunId, pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            await ClearPendingDefinitionQueryAsync(
                pending.RequestId,
                pending.RunEvent.RunId ?? string.Empty,
                "already_committed",
                CancellationToken.None);
            Logger.LogDebug(
                "Script definition query response ignored because run already committed. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                pending.RequestId);
            return;
        }

        if (!evt.Found)
        {
            await PersistPendingRunFailureAsync(
                pending,
                $"Definition snapshot query returned not found. request_id={pending.RequestId} reason={evt.FailureReason}",
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition snapshot query returned not found. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} reason={Reason}",
                Id,
                pending.RunEvent.RunId,
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
                pending,
                $"Definition snapshot query returned empty source. request_id={pending.RequestId}",
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition query returned empty source. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                evt.RequestId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.RunEvent.ScriptRevision) &&
            !string.Equals(pending.RunEvent.ScriptRevision, snapshot.Revision, StringComparison.Ordinal))
        {
            await PersistPendingRunFailureAsync(
                pending,
                "Definition snapshot revision mismatch. request_id=" + pending.RequestId +
                " requested_revision=" + pending.RunEvent.ScriptRevision +
                " actual_revision=" + snapshot.Revision,
                CancellationToken.None);
            Logger.LogWarning(
                "Script definition revision mismatch. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} requested_revision={RequestedRevision} actual_revision={ActualRevision}",
                Id,
                pending.RunEvent.RunId,
                evt.RequestId,
                pending.RunEvent.ScriptRevision,
                snapshot.Revision);
            return;
        }

        try
        {
            await PersistRunCommittedAsync(
                pending.RunEvent,
                snapshot,
                pending.RequestId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistPendingRunFailureAsync(
                pending,
                $"Script run execution failed after definition query. request_id={pending.RequestId} reason={ex.Message}",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Script run failed after definition query. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                pending.RequestId);
        }
    }

    private async Task QueueRunByDefinitionQueryAsync(RunScriptRequestedEvent evt, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var runEvent = evt.Clone();
        var queuedAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistDomainEventAsync(new ScriptDefinitionQueryQueuedEvent
        {
            RequestId = requestId,
            RunEvent = runEvent,
            QueuedAtUnixTimeMs = queuedAtUnixTimeMs,
        }, ct);
        var pending = new PendingRunContext(
            requestId,
            runEvent,
            queuedAtUnixTimeMs);

        try
        {
            await SendToAsync(
                evt.DefinitionActorId,
                new QueryScriptDefinitionSnapshotRequestedEvent
                {
                    RequestId = requestId,
                    ReplyStreamId = Id,
                    RequestedRevision = evt.ScriptRevision ?? string.Empty,
                },
                ct);
        }
        catch (Exception ex)
        {
            await PersistPendingRunFailureAsync(
                pending,
                $"Failed to dispatch definition query. request_id={requestId} reason={ex.Message}",
                CancellationToken.None);
            Logger.LogError(
                ex,
                "Failed to dispatch definition query. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                evt.RunId,
                requestId);
            return;
        }

        await ScheduleDefinitionQueryTimeoutAsync(
            requestId,
            evt.RunId ?? string.Empty,
            queuedAtUnixTimeMs,
            ct);

        Logger.LogInformation(
            "Script definition query dispatched. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} definition_actor_id={DefinitionActorId} revision={Revision}",
            Id,
            evt.RunId,
            requestId,
            evt.DefinitionActorId,
            evt.ScriptRevision);
    }

    [EventHandler]
    public async Task HandleScriptDefinitionQueryTimeoutFired(ScriptDefinitionQueryTimeoutFiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!TryGetPendingRunContext(evt.RequestId, out var pending))
        {
            Logger.LogDebug(
                "Ignoring stale script definition query timeout signal. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                evt.RequestId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.RunId) &&
            !string.Equals(evt.RunId, pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignoring mismatched script definition query timeout signal. runtime_actor_id={RuntimeActorId} request_id={RequestId} expected_run_id={ExpectedRunId} signal_run_id={SignalRunId}",
                Id,
                evt.RequestId,
                pending.RunEvent.RunId,
                evt.RunId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.RunEvent.RunId) &&
            string.Equals(State.LastRunId, pending.RunEvent.RunId, StringComparison.Ordinal))
        {
            await ClearPendingDefinitionQueryAsync(
                pending.RequestId,
                pending.RunEvent.RunId ?? string.Empty,
                "already_committed",
                CancellationToken.None);
            Logger.LogDebug(
                "Script definition query timeout signal ignored because run already committed. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
                Id,
                pending.RunEvent.RunId,
                pending.RequestId);
            return;
        }

        await PersistPendingRunFailureAsync(
            pending,
            $"Definition snapshot query timed out. request_id={pending.RequestId} timeout_seconds={PendingRunTimeout.TotalSeconds}",
            CancellationToken.None);
        Logger.LogWarning(
            "Script definition query timed out. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId}",
            Id,
            pending.RunEvent.RunId,
            pending.RequestId);
    }

    private async Task PersistRunCommittedAsync(
        RunScriptRequestedEvent runEvent,
        ScriptDefinitionSnapshot snapshot,
        string? completedQueryRequestId,
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
        if (string.IsNullOrWhiteSpace(completedQueryRequestId))
        {
            await PersistDomainEventsAsync(committedEvents, ct);
        }
        else
        {
            var eventsToPersist = new List<IMessage>(committedEvents.Count + 1);
            eventsToPersist.AddRange(committedEvents);
            eventsToPersist.Add(new ScriptDefinitionQueryClearedEvent
            {
                RequestId = completedQueryRequestId,
                RunId = runEvent.RunId ?? string.Empty,
                Reason = "completed",
            });
            await PersistDomainEventsAsync(eventsToPersist, ct);
        }

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            runEvent.RunId,
            committedEvents.Count,
            snapshot.Revision);
    }

    private async Task ScheduleDefinitionQueryTimeoutAsync(
        string requestId,
        string runId,
        long queuedAtUnixTimeMs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        var scheduler = Services.GetService<IActorRuntimeCallbackScheduler>();
        if (scheduler == null)
        {
            Logger.LogDebug(
                "Skipping script definition query timeout scheduling because runtime async scheduler is unavailable. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                requestId);
            return;
        }
        var queuedAt = queuedAtUnixTimeMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(queuedAtUnixTimeMs)
            : DateTimeOffset.UtcNow;
        var dueAt = queuedAt + PendingRunTimeout;
        var delay = dueAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
            delay = TimeSpan.FromMilliseconds(1);

        try
        {
            await scheduler.ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = Id,
                    CallbackId = BuildDefinitionQueryTimeoutCallbackId(requestId),
                    DueTime = delay,
                    TriggerEnvelope = new EventEnvelope
                    {
                        Payload = Any.Pack(new ScriptDefinitionQueryTimeoutFiredEvent
                        {
                            RequestId = requestId,
                            RunId = runId ?? string.Empty,
                        }),
                    },
                },
                ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to schedule script definition query timeout signal. runtime_actor_id={RuntimeActorId} request_id={RequestId} run_id={RunId}",
                Id,
                requestId,
                runId);
        }
    }

    private static string BuildDefinitionQueryTimeoutCallbackId(string requestId) =>
        string.Concat("script-definition-query-timeout:", requestId);

    private bool TryGetPendingRunContext(string requestId, out PendingRunContext pending)
    {
        pending = new PendingRunContext(
            string.Empty,
            new RunScriptRequestedEvent(),
            0);
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        if (!State.PendingDefinitionQueries.TryGetValue(requestId, out var stored) || stored?.RunEvent == null)
            return false;

        pending = new PendingRunContext(
            requestId,
            stored.RunEvent.Clone(),
            stored.QueuedAtUnixTimeMs);
        return true;
    }

    private async Task ClearPendingDefinitionQueryAsync(
        string requestId,
        string runId,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !State.PendingDefinitionQueries.ContainsKey(requestId))
            return;

        await PersistDomainEventAsync(new ScriptDefinitionQueryClearedEvent
        {
            RequestId = requestId,
            RunId = runId ?? string.Empty,
            Reason = reason ?? string.Empty,
        }, ct);
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
        PendingRunContext pending,
        string reason,
        CancellationToken ct)
    {
        await PersistDomainEventsAsync(
        [
            BuildRunFailureCommittedEvent(pending.RunEvent, reason),
            new ScriptDefinitionQueryClearedEvent
            {
                RequestId = pending.RequestId,
                RunId = pending.RunEvent.RunId ?? string.Empty,
                Reason = reason ?? string.Empty,
            },
        ], ct);
    }

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
            .On<ScriptRunDomainEventCommitted>(ApplyCommitted)
            .On<ScriptDefinitionQueryQueuedEvent>(ApplyDefinitionQueryQueued)
            .On<ScriptDefinitionQueryClearedEvent>(ApplyDefinitionQueryCleared)
            .OrCurrent();

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
        return next;
    }

    private static ScriptRuntimeState ApplyDefinitionQueryQueued(
        ScriptRuntimeState state,
        ScriptDefinitionQueryQueuedEvent evt)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(evt.RequestId) && evt.RunEvent != null)
        {
            next.PendingDefinitionQueries[evt.RequestId] = new PendingScriptDefinitionQueryState
            {
                RunEvent = evt.RunEvent.Clone(),
                QueuedAtUnixTimeMs = evt.QueuedAtUnixTimeMs,
            };
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat("definition-query:", evt.RequestId ?? string.Empty, ":queued");
        return next;
    }

    private static ScriptRuntimeState ApplyDefinitionQueryCleared(
        ScriptRuntimeState state,
        ScriptDefinitionQueryClearedEvent evt)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(evt.RequestId))
            next.PendingDefinitionQueries.Remove(evt.RequestId);

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat("definition-query:", evt.RequestId ?? string.Empty, ":cleared");
        return next;
    }

    private async Task<ScriptDefinitionSnapshot> LoadDefinitionSnapshotAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        return await _snapshotPort.GetRequiredAsync(definitionActorId, requestedRevision, ct);
    }

    private static IReadOnlyDictionary<string, Google.Protobuf.WellKnownTypes.Any> ClonePayloads(
        MapField<string, Google.Protobuf.WellKnownTypes.Any> payloads)
    {
        if (payloads.Count == 0)
            return new Dictionary<string, Google.Protobuf.WellKnownTypes.Any>(StringComparer.Ordinal);

        var clone = new Dictionary<string, Google.Protobuf.WellKnownTypes.Any>(
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
        MapField<string, Google.Protobuf.WellKnownTypes.Any> source,
        MapField<string, Google.Protobuf.WellKnownTypes.Any> target)
    {
        target.Clear();
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            target[key] = value.Clone();
        }
    }

    private sealed record PendingRunContext(
        string RequestId,
        RunScriptRequestedEvent RunEvent,
        long QueuedAtUnixTimeMs);
}
