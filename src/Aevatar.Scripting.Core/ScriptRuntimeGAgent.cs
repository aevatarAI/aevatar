using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    public ScriptRuntimeGAgent()
    {
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

        var snapshot = await LoadDefinitionSnapshotAsync(
            evt.DefinitionActorId,
            evt.ScriptRevision,
            CancellationToken.None);

        var orchestrator = Services.GetService(typeof(IScriptRuntimeExecutionOrchestrator))
            as IScriptRuntimeExecutionOrchestrator
            ?? throw new InvalidOperationException("IScriptRuntimeExecutionOrchestrator is required for ScriptRuntimeGAgent.");
        var committedEvents = await orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: Id,
                CurrentState: ClonePayloads(State.StatePayloads),
                CurrentReadModel: ClonePayloads(State.ReadModelPayloads),
                RunEvent: evt,
                ScriptId: snapshot.ScriptId,
                ScriptRevision: snapshot.Revision,
                SourceText: snapshot.SourceText,
                ReadModelSchemaVersion: snapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash: snapshot.ReadModelSchemaHash,
                Services: Services,
                EventPublisher: EventPublisher),
            CancellationToken.None);
        await PersistDomainEventsAsync(committedEvents, CancellationToken.None);

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            evt.RunId,
            committedEvents.Count,
            snapshot.Revision);
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
        var runtime = Services.GetService(typeof(IActorRuntime)) as IActorRuntime
            ?? throw new InvalidOperationException("IActorRuntime is required for loading script definition snapshot.");
        var definitionActor = await runtime.GetAsync(definitionActorId);
        if (definitionActor == null)
            throw new InvalidOperationException($"Script definition actor not found: {definitionActorId}");
        if (definitionActor.Agent is not ScriptDefinitionGAgent definitionAgent)
            throw new InvalidOperationException(
                $"Actor `{definitionActorId}` is not a ScriptDefinitionGAgent.");

        var state = definitionAgent.State;
        if (string.IsNullOrWhiteSpace(state.SourceText))
            throw new InvalidOperationException(
                $"Script definition source_text is empty for actor `{definitionActorId}`.");
        if (!string.IsNullOrWhiteSpace(requestedRevision) &&
            !string.Equals(requestedRevision, state.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Requested script revision `{requestedRevision}` does not match definition snapshot revision `{state.Revision}`.");

        ct.ThrowIfCancellationRequested();
        return new ScriptDefinitionSnapshot(
            state.ScriptId ?? string.Empty,
            state.Revision ?? string.Empty,
            state.SourceText ?? string.Empty,
            state.ReadModelSchemaVersion ?? string.Empty,
            state.ReadModelSchemaHash ?? string.Empty);
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

    private sealed record ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceText,
        string ReadModelSchemaVersion,
        string ReadModelSchemaHash);
}
