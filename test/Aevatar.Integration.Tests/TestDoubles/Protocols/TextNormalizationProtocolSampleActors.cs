using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests.TestDoubles.Protocols;

internal static class TextNormalizationProtocolSampleActors
{
    public static readonly string Source =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;

        public sealed class TextNormalizationBehavior : ScriptBehavior<TextNormalizationReadModel, TextNormalizationReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<TextNormalizationReadModel, TextNormalizationReadModel> builder)
            {
                builder
                    .OnCommand<TextNormalizationRequested>(HandleRequestedAsync)
                    .OnEvent<TextNormalizationCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<TextNormalizationQueryRequested, TextNormalizationQueryResponded>(HandleQueryAsync)
                    .DescribeReadModel(
                        new ScriptReadModelDefinition(
                            "text_normalization",
                            "2",
                            new[]
                            {
                                new ScriptReadModelFieldDefinition("last_command_id", "keyword", "last_command_id", false),
                                new ScriptReadModelFieldDefinition("input_text", "text", "input_text", false),
                                new ScriptReadModelFieldDefinition("normalized_text", "text", "normalized_text", false),
                                new ScriptReadModelFieldDefinition("lookup.normalized", "keyword", "lookup.normalized", false),
                                new ScriptReadModelFieldDefinition("refs.profile_id", "keyword", "refs.profile_id", false),
                            },
                            new[]
                            {
                                new ScriptReadModelIndexDefinition("idx_last_command", new[] { "last_command_id" }, true, "document"),
                                new ScriptReadModelIndexDefinition("idx_lookup_normalized", new[] { "lookup.normalized" }, false, "document"),
                            },
                            new[]
                            {
                                new ScriptReadModelRelationDefinition("rel_profile", "refs.profile_id", "profile", "profile_id", "many_to_one", "graph"),
                            }),
                        new[] { "document", "graph" });
            }

            private static Task HandleRequestedAsync(
                TextNormalizationRequested request,
                ScriptCommandContext<TextNormalizationReadModel> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                context.Emit(new TextNormalizationCompleted
                {
                    CommandId = request.CommandId,
                    Current = new TextNormalizationReadModel
                    {
                        HasValue = true,
                        LastCommandId = request.CommandId,
                        InputText = request.InputText,
                        NormalizedText = request.InputText.Trim().ToUpperInvariant(),
                        Lookup = new TextNormalizationLookup
                        {
                            Normalized = request.InputText.Trim().ToUpperInvariant(),
                        },
                        Refs = new TextNormalizationRefs
                        {
                            ProfileId = request.CommandId,
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<TextNormalizationQueryResponded?> HandleQueryAsync(
                TextNormalizationQueryRequested request,
                ScriptQueryContext<TextNormalizationReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<TextNormalizationQueryResponded?>(new TextNormalizationQueryResponded
                {
                    RequestId = request.RequestId,
                    Current = snapshot.CurrentReadModel ?? new TextNormalizationReadModel(),
                });
            }
        }
        """;

    public static readonly string SourceHash = ComputeSourceHash(Source);

    private static string ComputeSourceHash(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class TextNormalizationStaticGAgent : GAgentBase<TextNormalizationReadModel>
{
    [EventHandler]
    public async Task HandleRequested(TextNormalizationRequested evt)
    {
        var completed = TextNormalizationProtocolSample.BuildCompleted(evt);
        await PersistDomainEventAsync(completed, CancellationToken.None);
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
    private const string WorkflowYaml =
        """
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
            static (requestId, replyStreamId) => CreateEnvelope(
                new TextNormalizationQueryRequested
                {
                    RequestId = requestId,
                    ReplyStreamId = replyStreamId,
                }),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Text normalization worker query timed out. request_id={requestId}",
            CancellationToken.None);

        await PersistDomainEventAsync(new TextNormalizationCompleted
        {
            CommandId = evt.CommandId ?? string.Empty,
            Current = queryResponse.Current?.Clone() ?? new TextNormalizationReadModel(),
        }, CancellationToken.None);
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

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("workflow-protocol", TopologyAudience.Self),
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

    private readonly IScriptDefinitionCommandPort _definitionPort;
    private readonly IScriptRuntimeProvisioningPort _provisioningPort;
    private readonly IScriptRuntimeCommandPort _commandPort;
    private readonly IScriptReadModelQueryApplicationService _queryService;
    private readonly IScriptExecutionProjectionPort _projectionPort;

    public TextNormalizationScriptingProtocolGAgent(
        IScriptDefinitionCommandPort definitionPort,
        IScriptRuntimeProvisioningPort provisioningPort,
        IScriptRuntimeCommandPort commandPort,
        IScriptReadModelQueryApplicationService queryService,
        IScriptExecutionProjectionPort projectionPort)
    {
        _definitionPort = definitionPort ?? throw new ArgumentNullException(nameof(definitionPort));
        _provisioningPort = provisioningPort ?? throw new ArgumentNullException(nameof(provisioningPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    [EventHandler]
    public async Task HandleRequested(TextNormalizationRequested evt)
    {
        var definitionActorId = $"{Id}:script-definition";
        var runtimeActorId = $"{Id}:script-runtime";
        var runId = evt.CommandId ?? string.Empty;

        await _definitionPort.UpsertDefinitionAsync(
            ScriptId,
            ScriptRevision,
            TextNormalizationProtocolSampleActors.Source,
            TextNormalizationProtocolSampleActors.SourceHash,
            definitionActorId,
            CancellationToken.None);
        await _provisioningPort.EnsureRuntimeAsync(
            definitionActorId,
            ScriptRevision,
            runtimeActorId,
            CancellationToken.None);

        var lease = await _projectionPort.EnsureActorProjectionAsync(runtimeActorId, CancellationToken.None)
            ?? throw new InvalidOperationException("Script projection lease is required for text normalization sample.");
        await using var sink = new EventChannel<EventEnvelope>(capacity: 16);
        await _projectionPort.AttachLiveSinkAsync(lease, sink, CancellationToken.None);

        try
        {
            await _commandPort.RunRuntimeAsync(
                runtimeActorId,
                runId,
                Any.Pack(evt),
                ScriptRevision,
                definitionActorId,
                TextNormalizationRequested.Descriptor.FullName,
                CancellationToken.None);
            await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(
                sink,
                runId,
                CancellationToken.None);

            var snapshot = await _queryService.GetSnapshotAsync(runtimeActorId, CancellationToken.None)
                ?? throw new InvalidOperationException("Text normalization script read model snapshot was not produced.");

            await PersistDomainEventAsync(new TextNormalizationCompleted
            {
                CommandId = evt.CommandId ?? string.Empty,
                Current = snapshot.ReadModelPayload?.Unpack<TextNormalizationReadModel>() ?? new TextNormalizationReadModel(),
            }, CancellationToken.None);
        }
        finally
        {
            await _projectionPort.DetachLiveSinkAsync(lease, sink, CancellationToken.None);
            await _projectionPort.ReleaseActorProjectionAsync(lease, CancellationToken.None);
        }
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

internal static class TextNormalizationProtocolSample
{
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
                Lookup = new TextNormalizationLookup
                {
                    Normalized = Normalize(request.InputText ?? string.Empty),
                },
                Refs = new TextNormalizationRefs
                {
                    ProfileId = request.CommandId ?? string.Empty,
                },
            },
        };
    }

    public static string Normalize(string input) =>
        (input ?? string.Empty).Trim().ToUpperInvariant();
}
