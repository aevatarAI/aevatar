using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    private const string OrleansEventPublisherPrefix = "Aevatar.Foundation.Runtime.Implementations.Orleans.";

    private readonly IScriptRuntimeExecutionOrchestrator _orchestrator;
    private readonly IScriptDefinitionSnapshotPort _snapshotPort;
    private readonly Dictionary<string, PendingRunContext> _pendingRuns = new(StringComparer.Ordinal);

    public ScriptRuntimeGAgent(
        IScriptRuntimeExecutionOrchestrator orchestrator,
        IScriptDefinitionSnapshotPort snapshotPort)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _snapshotPort = snapshotPort ?? throw new ArgumentNullException(nameof(snapshotPort));
        InitializeId();
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

        if (ShouldUseEventDrivenDefinitionQuery())
        {
            await QueueRunByDefinitionQueryAsync(evt, CancellationToken.None);
            return;
        }

        var snapshot = await LoadDefinitionSnapshotAsync(
            evt.DefinitionActorId,
            evt.ScriptRevision,
            CancellationToken.None);
        await ExecuteRunAsync(evt, snapshot, CancellationToken.None);
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

        if (string.IsNullOrWhiteSpace(evt.RequestId) || !_pendingRuns.Remove(evt.RequestId, out var pending))
        {
            Logger.LogDebug(
                "Ignoring unmatched script definition snapshot response. runtime_actor_id={RuntimeActorId} request_id={RequestId}",
                Id,
                evt.RequestId);
            return;
        }

        if (!evt.Found)
        {
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
            Logger.LogWarning(
                "Script definition revision mismatch. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} requested_revision={RequestedRevision} actual_revision={ActualRevision}",
                Id,
                pending.RunEvent.RunId,
                evt.RequestId,
                pending.RunEvent.ScriptRevision,
                snapshot.Revision);
            return;
        }

        await ExecuteRunAsync(pending.RunEvent, snapshot, CancellationToken.None);
    }

    private async Task QueueRunByDefinitionQueryAsync(RunScriptRequestedEvent evt, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _pendingRuns[requestId] = new PendingRunContext(evt.Clone());

        await SendToAsync(
            evt.DefinitionActorId,
            new QueryScriptDefinitionSnapshotRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = Id,
                RequestedRevision = evt.ScriptRevision ?? string.Empty,
            },
            ct);

        Logger.LogInformation(
            "Script definition query dispatched. runtime_actor_id={RuntimeActorId} run_id={RunId} request_id={RequestId} definition_actor_id={DefinitionActorId} revision={Revision}",
            Id,
            evt.RunId,
            requestId,
            evt.DefinitionActorId,
            evt.ScriptRevision);
    }

    private async Task ExecuteRunAsync(
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

    private bool ShouldUseEventDrivenDefinitionQuery()
    {
        var publisherType = EventPublisher.GetType().FullName;
        return !string.IsNullOrWhiteSpace(publisherType) &&
               publisherType.StartsWith(OrleansEventPublisherPrefix, StringComparison.Ordinal);
    }

    protected override ScriptRuntimeState TransitionState(ScriptRuntimeState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptRunDomainEventCommitted>(ApplyCommitted)
            .OrCurrent();

    private static ScriptRuntimeState ApplyCommitted(
        ScriptRuntimeState state,
        ScriptRunDomainEventCommitted committed)
    {
        var next = state.Clone();
        next.DefinitionActorId = committed.DefinitionActorId ?? string.Empty;
        next.Revision = committed.ScriptRevision ?? string.Empty;
        next.LastRunId = committed.RunId ?? string.Empty;
        CopyPayloads(committed.StatePayloads, next.StatePayloads);
        CopyPayloads(committed.ReadModelPayloads, next.ReadModelPayloads);
        next.LastAppliedSchemaVersion = committed.ReadModelSchemaVersion ?? string.Empty;
        next.LastSchemaHash = committed.ReadModelSchemaHash ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = committed.RunId ?? string.Empty;
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

    private sealed record PendingRunContext(RunScriptRequestedEvent RunEvent);
}
