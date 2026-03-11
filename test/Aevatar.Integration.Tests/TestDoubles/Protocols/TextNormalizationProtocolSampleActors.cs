using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Integration.Tests;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Workflow.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests.TestDoubles.Protocols;

public sealed class TextNormalizationStaticGAgent : GAgentBase<TextNormalizationReadModel>
{
    [EventHandler]
    public async Task HandleRequested(TextNormalizationRequested evt)
    {
        var completed = TextNormalizationProtocolSample.BuildCompleted(evt);
        await PersistDomainEventAsync(completed, CancellationToken.None);
        await PublishAsync(completed, EventDirection.Self, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleQueryRequested(TextNormalizationQueryRequested evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        return EventPublisher.SendToAsync(
            evt.ReplyStreamId,
            new TextNormalizationQueryResponded
            {
                RequestId = evt.RequestId,
                Current = State.Clone(),
            },
            CancellationToken.None,
            sourceEnvelope: null);
    }

    protected override TextNormalizationReadModel TransitionState(
        TextNormalizationReadModel current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<TextNormalizationCompleted>((_, completed) => completed.Current?.Clone() ?? new TextNormalizationReadModel())
            .OrCurrent();
}

public sealed class TextNormalizationWorkflowProtocolGAgent : GAgentBase<TextNormalizationReadModel>
{
    private const string WorkflowName = "text_normalization_protocol";
    private const string SendStateKey = "protocol.send";
    private const string WorkflowYaml = """
        name: text_normalization_protocol
        roles: []
        steps:
          - id: send_command
            type: actor_send
            parameters:
              send_state_key: protocol.send
        """;

    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly IStreamRequestReplyClient _requestReplyClient;

    public TextNormalizationWorkflowProtocolGAgent(
        IActorRuntime runtime,
        IStreamProvider streams,
        IStreamRequestReplyClient requestReplyClient)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _requestReplyClient = requestReplyClient ?? throw new ArgumentNullException(nameof(requestReplyClient));
    }

    [EventHandler]
    public async Task HandleRequested(TextNormalizationRequested evt)
    {
        var definitionActor = await EnsureWorkflowDefinitionAsync();
        var runActor = await EnsureWorkflowRunAsync();
        var workerActor = await EnsureWorkerActorAsync();
        var runAgent = (WorkflowRunGAgent)runActor.Agent;
        var definitionAgent = (WorkflowGAgent)definitionActor.Agent;

        await definitionAgent.BindWorkflowDefinitionAsync(
            WorkflowYaml,
            WorkflowName,
            ct: CancellationToken.None);
        await runAgent.BindWorkflowRunDefinitionAsync(
            definitionActor.Id,
            WorkflowYaml,
            WorkflowName,
            runId: runActor.Id,
            ct: CancellationToken.None);
        await runAgent.UpsertExecutionStateAsync(
            SendStateKey,
            Any.Pack(new ActorSendState
            {
                TargetActorId = workerActor.Id,
                Payload = Any.Pack(evt),
            }),
            CancellationToken.None);

        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _streams
            .GetStream(runActor.Id)
            .SubscribeAsync<WorkflowCompletedEvent>(completed =>
            {
                workflowCompleted.TrySetResult(completed);
                return Task.CompletedTask;
            });

        await runAgent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = evt.InputText,
            SessionId = evt.CommandId,
        });

        var result = await workflowCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!result.Success)
            throw new InvalidOperationException($"Workflow protocol sample failed: {result.Error}");

        var queryResponse = await _requestReplyClient.QueryActorAsync<TextNormalizationQueryResponded>(
            _streams,
            workerActor,
            "text-normalization-worker-query",
            TimeSpan.FromSeconds(5),
            static (requestId, replyStreamId) => CreateProtocolEnvelope(
                new TextNormalizationQueryRequested
                {
                    RequestId = requestId,
                    ReplyStreamId = replyStreamId,
                }),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Text normalization worker query timed out. request_id={requestId}",
            CancellationToken.None);
        var current = queryResponse.Current?.Clone()
                      ?? new TextNormalizationReadModel();

        var completed = new TextNormalizationCompleted
        {
            CommandId = evt.CommandId ?? string.Empty,
            Current = current,
        };
        await PersistDomainEventAsync(completed, CancellationToken.None);
        await PublishAsync(completed, EventDirection.Self, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleQueryRequested(TextNormalizationQueryRequested evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        return EventPublisher.SendToAsync(
            evt.ReplyStreamId,
            new TextNormalizationQueryResponded
            {
                RequestId = evt.RequestId,
                Current = State.Clone(),
            },
            CancellationToken.None,
            sourceEnvelope: null);
    }

    protected override TextNormalizationReadModel TransitionState(
        TextNormalizationReadModel current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<TextNormalizationCompleted>((_, completed) => completed.Current?.Clone() ?? new TextNormalizationReadModel())
            .OrCurrent();

    private async Task<IActor> EnsureWorkerActorAsync()
    {
        var actorId = $"{Id}:static-worker";
        return await _runtime.GetAsync(actorId)
               ?? await _runtime.CreateAsync<TextNormalizationStaticGAgent>(actorId, CancellationToken.None);
    }

    private async Task<IActor> EnsureWorkflowDefinitionAsync()
    {
        var actorId = $"{Id}:workflow-definition";
        return await _runtime.GetAsync(actorId)
               ?? await _runtime.CreateAsync<WorkflowGAgent>(actorId, CancellationToken.None);
    }

    private async Task<IActor> EnsureWorkflowRunAsync()
    {
        var actorId = $"{Id}:workflow-run";
        return await _runtime.GetAsync(actorId)
               ?? await _runtime.CreateAsync<WorkflowRunGAgent>(actorId, CancellationToken.None);
    }

    private static EventEnvelope CreateProtocolEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                PublisherActorId = "workflow-protocol",
                Direction = EventDirection.Self,
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
}

public sealed class TextNormalizationScriptingProtocolGAgent : GAgentBase<TextNormalizationReadModel>
{
    private const string ScriptId = "text-normalization-protocol-script";
    private const string ScriptRevision = "rev-1";
    private const string ReadModelPayloadKey = "text_normalization.current";

    private readonly IActorRuntime _runtime;

    public TextNormalizationScriptingProtocolGAgent(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    [EventHandler]
    public async Task HandleRequested(TextNormalizationRequested evt)
    {
        var definitionActor = await EnsureDefinitionActorAsync();
        var runtimeActor = await EnsureRuntimeActorAsync();
        var runId = evt.CommandId ?? string.Empty;

        await definitionActor.HandleEventAsync(
            ScriptingCommandEnvelopeTestKit.CreateUpsertDefinition(
                definitionActor.Id,
                ScriptId,
                ScriptRevision,
                TextNormalizationProtocolSample.ScriptSource,
                "text-normalization-protocol-hash"),
            CancellationToken.None);
        await runtimeActor.HandleEventAsync(
            ScriptingCommandEnvelopeTestKit.CreateRunScript(
                runtimeActor.Id,
                runId,
                Any.Pack(evt),
                ScriptRevision,
                definitionActor.Id,
                TextNormalizationRequested.Descriptor.FullName),
            CancellationToken.None);

        var runtimeAgent = (ScriptRuntimeGAgent)runtimeActor.Agent;
        if (!runtimeAgent.State.ReadModelPayloads.TryGetValue(ReadModelPayloadKey, out var packedReadModel))
        {
            throw new InvalidOperationException("Scripting protocol sample did not persist read model payload.");
        }

        var completed = new TextNormalizationCompleted
        {
            CommandId = evt.CommandId ?? string.Empty,
            Current = packedReadModel.Unpack<TextNormalizationReadModel>(),
        };
        await PersistDomainEventAsync(completed, CancellationToken.None);
        await PublishAsync(completed, EventDirection.Self, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleQueryRequested(TextNormalizationQueryRequested evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        return EventPublisher.SendToAsync(
            evt.ReplyStreamId,
            new TextNormalizationQueryResponded
            {
                RequestId = evt.RequestId,
                Current = State.Clone(),
            },
            CancellationToken.None,
            sourceEnvelope: null);
    }

    protected override TextNormalizationReadModel TransitionState(
        TextNormalizationReadModel current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<TextNormalizationCompleted>((_, completed) => completed.Current?.Clone() ?? new TextNormalizationReadModel())
            .OrCurrent();

    private async Task<IActor> EnsureDefinitionActorAsync()
    {
        var actorId = $"{Id}:script-definition";
        return await _runtime.GetAsync(actorId)
               ?? await _runtime.CreateAsync<ScriptDefinitionGAgent>(actorId, CancellationToken.None);
    }

    private async Task<IActor> EnsureRuntimeActorAsync()
    {
        var actorId = $"{Id}:script-runtime";
        return await _runtime.GetAsync(actorId)
               ?? await _runtime.CreateAsync<ScriptRuntimeGAgent>(actorId, CancellationToken.None);
    }
}

internal static class TextNormalizationProtocolSample
{
    public static readonly string ScriptSource = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf;
        using Google.Protobuf.WellKnownTypes;

        public sealed class TextNormalizationProtocolScript : IScriptPackageRuntime
        {
            public Task<ScriptHandlerResult> HandleRequestedEventAsync(
                ScriptRequestedEventEnvelope requestedEvent,
                ScriptExecutionContext context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var request = context.InputPayload != null && context.InputPayload.Is(TextNormalizationRequested.Descriptor)
                    ? context.InputPayload.Unpack<TextNormalizationRequested>()
                    : new TextNormalizationRequested();
                var current = new TextNormalizationReadModel
                {
                    HasValue = true,
                    LastCommandId = request.CommandId ?? string.Empty,
                    InputText = request.InputText ?? string.Empty,
                    NormalizedText = Normalize(request.InputText ?? string.Empty),
                };
                return Task.FromResult(new ScriptHandlerResult(
                    new IMessage[]
                    {
                        new TextNormalizationCompleted
                        {
                            CommandId = request.CommandId ?? string.Empty,
                            Current = current,
                        },
                    }));
            }

            public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
                IReadOnlyDictionary<string, Any> currentState,
                ScriptDomainEventEnvelope domainEvent,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (domainEvent.Payload == null || !domainEvent.Payload.Is(TextNormalizationCompleted.Descriptor))
                    return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

                var completed = domainEvent.Payload.Unpack<TextNormalizationCompleted>();
                return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
                    new Dictionary<string, Any>
                    {
                        ["text_normalization.current"] = Any.Pack(completed.Current ?? new TextNormalizationReadModel()),
                    });
            }

            public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
                IReadOnlyDictionary<string, Any> currentReadModel,
                ScriptDomainEventEnvelope domainEvent,
                CancellationToken ct)
                => ApplyDomainEventAsync(currentReadModel, domainEvent, ct);

            private static string Normalize(string input) =>
                (input ?? string.Empty).Trim().ToUpperInvariant();
        }
        """;

    public static TextNormalizationCompleted BuildCompleted(TextNormalizationRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new TextNormalizationCompleted
        {
            CommandId = request.CommandId ?? string.Empty,
            Current = new TextNormalizationReadModel
            {
                HasValue = true,
                LastCommandId = request.CommandId ?? string.Empty,
                InputText = request.InputText ?? string.Empty,
                NormalizedText = Normalize(request.InputText ?? string.Empty),
            },
        };
    }

    public static string Normalize(string input) =>
        (input ?? string.Empty).Trim().ToUpperInvariant();
}
