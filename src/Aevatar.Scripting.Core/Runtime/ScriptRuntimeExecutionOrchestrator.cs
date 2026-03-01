using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptRuntimeExecutionOrchestrator : IScriptRuntimeExecutionOrchestrator
{
    private static readonly IReadOnlyDictionary<string, Any> EmptyPayloads =
        new Dictionary<string, Any>(StringComparer.Ordinal);

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
            CurrentState: NormalizePayloads(request.CurrentState),
            CurrentReadModel: NormalizePayloads(request.CurrentReadModel),
            InputPayload: request.RunEvent.InputPayload?.Clone(),
            Capabilities: capabilities);

        var requestedEvent = new ScriptRequestedEventEnvelope(
            EventType: string.IsNullOrWhiteSpace(request.RunEvent.RequestedEventType)
                ? "script.run.requested"
                : request.RunEvent.RequestedEventType,
            Payload: request.RunEvent.InputPayload?.Clone() ?? Any.Pack(new Empty()),
            EventId: runId,
            CorrelationId: correlationId,
            CausationId: runId);

        var decision = await compilation.CompiledDefinition.HandleRequestedEventAsync(requestedEvent, context, ct);

        return await BuildCommittedEventsAsync(
            compilation.CompiledDefinition,
            request.RunEvent,
            request.ScriptRevision,
            request.ReadModelSchemaVersion,
            request.ReadModelSchemaHash,
            request.CurrentState,
            request.CurrentReadModel,
            decision,
            ct);
    }

    private static async Task<IReadOnlyList<IMessage>> BuildCommittedEventsAsync(
        IScriptPackageDefinition definition,
        RunScriptRequestedEvent run,
        string revision,
        string readModelSchemaVersion,
        string readModelSchemaHash,
        IReadOnlyDictionary<string, Any>? initialState,
        IReadOnlyDictionary<string, Any>? initialReadModel,
        ScriptHandlerResult decision,
        CancellationToken ct)
    {
        var domainEvents = decision.DomainEvents ?? [];
        if (domainEvents.Count == 0)
            domainEvents = [new StringValue { Value = "script.run.completed" }];

        var committed = new List<IMessage>(domainEvents.Count);
        var currentState = NormalizePayloads(initialState);
        var currentReadModel = NormalizePayloads(initialReadModel);
        var decisionStatePayloads = NormalizePayloads(decision.StatePayloads);
        var decisionReadModelPayloads = NormalizePayloads(decision.ReadModelPayloads);

        for (var i = 0; i < domainEvents.Count; i++)
        {
            var domainEvent = domainEvents[i];
            var eventType = ResolveEventType(domainEvent);
            var payload = ResolvePayload(domainEvent);
            var domainEnvelope = new ScriptDomainEventEnvelope(
                EventType: eventType,
                Payload: payload,
                EventId: BuildEventId(run.RunId ?? string.Empty, i),
                CorrelationId: run.RunId ?? string.Empty,
                CausationId: run.RunId ?? string.Empty);

            var appliedState = await definition.ApplyDomainEventAsync(currentState, domainEnvelope, ct);
            appliedState ??= currentState;
            if (appliedState.Count == 0 && decisionStatePayloads.Count > 0)
                appliedState = decisionStatePayloads;

            var reducedReadModel = await definition.ReduceReadModelAsync(currentReadModel, domainEnvelope, ct);
            reducedReadModel ??= currentReadModel;
            if (reducedReadModel.Count == 0 && decisionReadModelPayloads.Count > 0)
                reducedReadModel = decisionReadModelPayloads;

            currentState = NormalizePayloads(appliedState);
            currentReadModel = NormalizePayloads(reducedReadModel);

            var committedEvent = new ScriptRunDomainEventCommitted
            {
                RunId = run.RunId ?? string.Empty,
                ScriptRevision = revision,
                DefinitionActorId = run.DefinitionActorId ?? string.Empty,
                EventType = eventType,
                Payload = payload,
                ReadModelSchemaVersion = readModelSchemaVersion ?? string.Empty,
                ReadModelSchemaHash = readModelSchemaHash ?? string.Empty,
            };
            CopyPayloads(currentState, committedEvent.StatePayloads);
            CopyPayloads(currentReadModel, committedEvent.ReadModelPayloads);
            committed.Add(committedEvent);
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

    private static Any ResolvePayload(IMessage domainEvent)
    {
        return Any.Pack(domainEvent);
    }

    private static IReadOnlyDictionary<string, Any> NormalizePayloads(
        IReadOnlyDictionary<string, Any>? payloads)
    {
        if (payloads == null || payloads.Count == 0)
            return EmptyPayloads;

        var normalized = new Dictionary<string, Any>(payloads.Count, StringComparer.Ordinal);
        foreach (var (key, value) in payloads)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
                continue;

            normalized[key] = value.Clone();
        }

        return normalized;
    }

    private static void CopyPayloads(
        IReadOnlyDictionary<string, Any> source,
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
}
