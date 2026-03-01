using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptRuntimeExecutionOrchestrator : IScriptRuntimeExecutionOrchestrator
{
    private readonly IScriptPackageCompiler _compiler;
    private readonly IScriptCapabilityFactory _capabilityFactory;

    public ScriptRuntimeExecutionOrchestrator(
        IScriptPackageCompiler compiler,
        IScriptCapabilityFactory capabilityFactory)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
    }

    public async Task<IReadOnlyList<IMessage>> ExecuteRunAsync(
        ScriptRuntimeExecutionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var compilation = await _compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                request.ScriptId,
                request.ScriptRevision,
                request.SourceText),
            ct);
        if (!compilation.IsSuccess || compilation.CompiledDefinition == null)
            throw new InvalidOperationException(
                "Script compilation failed in runtime: " + string.Join("; ", compilation.Diagnostics));

        var runId = request.RunEvent.RunId ?? string.Empty;
        var correlationId = runId;
        var capabilities = _capabilityFactory.Create(
            request.RuntimeActorId,
            runId,
            correlationId,
            request.Services);

        var context = new ScriptExecutionContext(
            ActorId: request.RuntimeActorId,
            ScriptId: request.ScriptId,
            Revision: request.ScriptRevision,
            RunId: runId,
            CorrelationId: correlationId,
            DefinitionActorId: request.RunEvent.DefinitionActorId ?? string.Empty,
            CurrentStateJson: request.CurrentStateJson ?? string.Empty,
            CurrentReadModelJson: request.CurrentReadModelJson ?? string.Empty,
            InputJson: request.RunEvent.InputJson ?? string.Empty,
            Capabilities: capabilities);

        var requestedEvent = new ScriptRequestedEventEnvelope(
            EventType: string.IsNullOrWhiteSpace(request.RunEvent.RequestedEventType)
                ? "script.run.requested"
                : request.RunEvent.RequestedEventType,
            PayloadJson: request.RunEvent.InputJson ?? string.Empty,
            EventId: runId,
            CorrelationId: correlationId,
            CausationId: runId);

        var decision = await compilation.CompiledDefinition.HandleRequestedEventAsync(requestedEvent, context, ct);

        return await BuildCommittedEventsAsync(
            compilation.CompiledDefinition,
            request.RunEvent,
            request.ScriptRevision,
            request.CurrentStateJson ?? string.Empty,
            request.CurrentReadModelJson ?? string.Empty,
            decision,
            ct);
    }

    private static async Task<IReadOnlyList<IMessage>> BuildCommittedEventsAsync(
        IScriptPackageDefinition definition,
        RunScriptRequestedEvent run,
        string revision,
        string initialStateJson,
        string initialReadModelJson,
        ScriptHandlerResult decision,
        CancellationToken ct)
    {
        var domainEvents = decision.DomainEvents ?? [];
        if (domainEvents.Count == 0)
            domainEvents = [new StringValue { Value = "script.run.completed" }];

        var committed = new List<IMessage>(domainEvents.Count);
        var currentState = initialStateJson ?? string.Empty;
        var currentReadModel = initialReadModelJson ?? string.Empty;

        for (var i = 0; i < domainEvents.Count; i++)
        {
            var domainEvent = domainEvents[i];
            var eventType = ResolveEventType(domainEvent);
            var payloadJson = ResolvePayloadJson(domainEvent);
            var domainEnvelope = new ScriptDomainEventEnvelope(
                EventType: eventType,
                PayloadJson: payloadJson,
                EventId: BuildEventId(run.RunId ?? string.Empty, i),
                CorrelationId: run.RunId ?? string.Empty,
                CausationId: run.RunId ?? string.Empty);

            var appliedState = await definition.ApplyDomainEventAsync(currentState, domainEnvelope, ct);
            if (string.IsNullOrWhiteSpace(appliedState))
                appliedState = currentState;
            if (string.IsNullOrWhiteSpace(appliedState) && !string.IsNullOrWhiteSpace(decision.StatePayloadJson))
                appliedState = decision.StatePayloadJson;

            var reducedReadModel = await definition.ReduceReadModelAsync(currentReadModel, domainEnvelope, ct);
            if (string.IsNullOrWhiteSpace(reducedReadModel))
                reducedReadModel = currentReadModel;
            if (string.IsNullOrWhiteSpace(reducedReadModel) && !string.IsNullOrWhiteSpace(decision.ReadModelPayloadJson))
                reducedReadModel = decision.ReadModelPayloadJson;

            currentState = appliedState ?? string.Empty;
            currentReadModel = reducedReadModel ?? string.Empty;

            committed.Add(new ScriptRunDomainEventCommitted
            {
                RunId = run.RunId ?? string.Empty,
                ScriptRevision = revision,
                DefinitionActorId = run.DefinitionActorId ?? string.Empty,
                EventType = eventType,
                PayloadJson = payloadJson,
                StatePayloadJson = currentState,
                ReadModelPayloadJson = currentReadModel,
            });
        }

        return committed;
    }

    private static string BuildEventId(string runId, int index)
    {
        var normalizedRunId = runId ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalizedRunId)
            ? $"script-run-event-{index + 1}"
            : $"{normalizedRunId}:{index + 1}";
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
}
