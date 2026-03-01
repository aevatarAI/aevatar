using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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

        var compiler = Services.GetService(typeof(IScriptAgentCompiler)) as IScriptAgentCompiler
            ?? throw new InvalidOperationException("IScriptAgentCompiler is required for ScriptRuntimeGAgent.");
        var compilation = await compiler.CompileAsync(
            new ScriptCompilationRequest(
                snapshot.ScriptId,
                snapshot.Revision,
                snapshot.SourceText),
            CancellationToken.None);

        if (!compilation.IsSuccess || compilation.CompiledDefinition == null)
            throw new InvalidOperationException(
                "Script compilation failed in runtime: " + string.Join("; ", compilation.Diagnostics));

        var context = new Aevatar.Scripting.Abstractions.Definitions.ScriptExecutionContext(
            ActorId: Id,
            ScriptId: snapshot.ScriptId,
            Revision: snapshot.Revision,
            RunId: evt.RunId ?? string.Empty,
            CorrelationId: evt.RunId ?? string.Empty,
            DefinitionActorId: evt.DefinitionActorId ?? string.Empty,
            CurrentStateJson: State.StatePayloadJson ?? string.Empty,
            InputJson: evt.InputJson ?? string.Empty,
            Capabilities: CreateRuntimeCapabilities(evt));

        var decision = await compilation.CompiledDefinition.DecideAsync(context, CancellationToken.None);
        var committedEvents = BuildCommittedEvents(evt, snapshot.Revision, decision);
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
        if (!string.IsNullOrWhiteSpace(committed.StatePayloadJson))
            next.StatePayloadJson = committed.StatePayloadJson ?? string.Empty;
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
            state.SourceText ?? string.Empty);
    }

    private static IReadOnlyList<IMessage> BuildCommittedEvents(
        RunScriptRequestedEvent run,
        string revision,
        Aevatar.Scripting.Abstractions.Definitions.ScriptDecisionResult decision)
    {
        var domainEvents = decision.DomainEvents ?? [];
        var statePayloadJson = decision.StatePayloadJson ?? string.Empty;
        if (domainEvents.Count == 0)
        {
            const string completedPayload = "{\"result\":\"ok\"}";
            return
            [
                new ScriptRunDomainEventCommitted
                {
                    RunId = run.RunId ?? string.Empty,
                    ScriptRevision = revision,
                    DefinitionActorId = run.DefinitionActorId ?? string.Empty,
                    EventType = "script.run.completed",
                    PayloadJson = completedPayload,
                    StatePayloadJson = string.IsNullOrWhiteSpace(statePayloadJson) ? completedPayload : statePayloadJson,
                }
            ];
        }

        var committed = new List<IMessage>(domainEvents.Count);
        foreach (var domainEvent in domainEvents)
        {
            var eventType = ResolveEventType(domainEvent);
            var payloadJson = ResolvePayloadJson(domainEvent);
            var committedStatePayload = string.IsNullOrWhiteSpace(statePayloadJson) ? payloadJson : statePayloadJson;
            committed.Add(new ScriptRunDomainEventCommitted
            {
                RunId = run.RunId ?? string.Empty,
                ScriptRevision = revision,
                DefinitionActorId = run.DefinitionActorId ?? string.Empty,
                EventType = eventType,
                PayloadJson = payloadJson,
                StatePayloadJson = committedStatePayload,
            });
        }

        return committed;
    }

    private static string ResolveEventType(IMessage domainEvent)
    {
        if (domainEvent is StringValue namedEvent)
            return namedEvent.Value ?? string.Empty;

        return domainEvent.Descriptor?.Name ?? domainEvent.GetType().Name;
    }

    private static string ResolvePayloadJson(IMessage domainEvent)
    {
        if (domainEvent is StringValue namedEvent)
        {
            return JsonSerializer.Serialize(new
            {
                event_type = namedEvent.Value ?? string.Empty,
            });
        }

        return JsonFormatter.Default.Format(domainEvent);
    }

    private Aevatar.Scripting.Abstractions.Definitions.IScriptRuntimeCapabilities CreateRuntimeCapabilities(
        RunScriptRequestedEvent evt)
    {
        return new ScriptRuntimeCapabilities(
            evt.RunId ?? string.Empty,
            evt.RunId ?? string.Empty,
            Services);
    }

    private sealed record ScriptDefinitionSnapshot(
        string ScriptId,
        string Revision,
        string SourceText);
}
