using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowDraftRunTests
{
    [Theory]
    [InlineData("/workflow run daily-greeting", "daily-greeting")]
    [InlineData("/run-workflow daily.greeting", "daily.greeting")]
    public void IntentParser_ShouldMatchRegisteredWorkflowRunSlashCommand(string text, string workflowId)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry());

        var matched = parser.TryParse(text, out var intent);

        matched.Should().BeTrue();
        intent.WorkflowId.Should().Be(workflowId);
        intent.Prompt.Should().Be(text);
    }

    [Theory]
    [InlineData("please run daily-greeting workflow")]
    [InlineData("跑一下 daily_greeting 的 workflow")]
    [InlineData("run daily-greeting workflow")]
    [InlineData("/workflow run")]
    [InlineData("/workflow run daily greeting")]
    [InlineData("aevatar_start_workflow daily-greeting")]
    public void IntentParser_ShouldRejectUnregisteredNaturalLanguageOrToolNamedText(string text)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry());

        parser.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-scope", null, "user-token-1", true, true, "scope")]
    [InlineData("missing-token", "scope-1", null, true, true, "授权")]
    [InlineData("missing-query-port", "scope-1", "user-token-1", false, true, "查询服务")]
    [InlineData("workflow-not-found", "scope-1", "user-token-1", true, false, "未找到")]
    [InlineData("workflow-actor-missing", "scope-1", "user-token-1", true, true, "actor")]
    public async Task Admission_ShouldReturnUserVisibleRejection_ForUnavailableWorkflowDraftRunInputs(
        string caseName,
        string? scopeId,
        string? userToken,
        bool registerQueryPort,
        bool workflowExists,
        string expectedText)
    {
        var workflow = workflowExists
            ? BuildWorkflowSummary(
                "scope-1",
                "daily-greeting",
                actorId: caseName == "workflow-actor-missing" ? string.Empty : "actor-daily-greeting")
            : null;
        var admission = new ChannelWorkflowDraftRunAdmission(
            new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry()),
            registerQueryPort ? new StubScopeWorkflowQueryPort(workflow) : null);
        var activity = BuildWorkflowActivity(scopeId, userToken);
        var runtimeContext = string.IsNullOrWhiteSpace(userToken)
            ? ConversationTurnRuntimeContext.Empty
            : new ConversationTurnRuntimeContext(null, userToken);

        var result = await admission.TryAdmitAsync(
            activity,
            new ChannelBotRegistrationEntry { Id = "reg-1", ScopeId = scopeId ?? string.Empty },
            new ChannelInboundEvent
            {
                Text = "/workflow run daily-greeting",
                Platform = "lark",
                MessageId = "msg-1",
                ConversationId = "oc_group_chat_1",
            },
            runtimeContext,
            CancellationToken.None);

        result.Matched.Should().BeTrue();
        result.Request.Should().BeNull();
        result.Rejection.Should().NotBeNull();
        result.Rejection!.Text.Should().Contain(expectedText);
    }

    [Fact]
    public async Task InteractionPort_ShouldDispatchAcceptedStartToRunScopedActor()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ChannelWorkflowDraftRunInteractionPort(
            actorRuntime,
            dispatch,
            NullLogger<ChannelWorkflowDraftRunInteractionPort>.Instance,
            TimeProvider.System);
        var request = BuildWorkflowRequest();

        await port.DispatchAsync(request, CancellationToken.None);

        var runActorId = "channel-workflow-draft-run:workflow-draft-run-1";
        actorRuntime.Created.Should().ContainSingle().Which.Should().Be(runActorId);
        dispatch.Envelopes.Should().ContainSingle();
        dispatch.Envelopes[0].ActorId.Should().Be(runActorId);
        dispatch.Envelopes[0].Envelope.Runtime.Deduplication.OperationId.Should()
            .Be("workflow-draft-run-start:workflow-draft-run-1");
        var command = dispatch.Envelopes[0].Envelope.Payload.Unpack<ChannelWorkflowDraftRunStartRequested>();
        command.RunId.Should().Be("workflow-draft-run-1");
        command.Request.TargetActorId.Should().Be("conversation-actor-1");
        command.Request.ReplyToken.Should().Be("reply-token-1");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldBuildWorkflowRequestAndRenderFramesIntoConversationCarriers()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(
            dispatch,
            workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = request.RunId,
        });

        workflow.LastRequest.Should().NotBeNull();
        workflow.LastRequest!.Prompt.Should().Be("/workflow run daily-greeting");
        workflow.LastRequest.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        workflow.LastRequest.Source.ActorId.Should().Be("workflow-actor-1");
        workflow.LastRequest.ScopeId.Should().Be("scope-1");
        workflow.LastRequest.CallerCredential!.BearerToken.Should().Be("user-token-1");
        workflow.LastRequest.CommandIdSeed.Should().Be("workflow-draft-run-1");
        workflow.LastRequest.CorrelationIdSeed.Should().Be("msg-1");
        workflow.LastRequest.Headers!["registration_id"].Should().Be("reg-1");

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes[0].ActorId.Should().Be("conversation-actor-1");
        var firstChunk = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        firstChunk.AccumulatedText.Should().Be("hello");
        firstChunk.ReplyToken.Should().Be("reply-token-1");

        var secondChunk = dispatch.Envelopes[1].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        secondChunk.AccumulatedText.Should().Be("hello world");

        var ready = dispatch.Envelopes[2].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.RunId.Should().Be("workflow-draft-run-1");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        ready.Outbound.Text.Should().Be("hello world");
        ready.ReplyToken.Should().Be("reply-token-1");

        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.ReplyHandedOff);
        agent.State.RunId.Should().Be("workflow-draft-run-1");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowPortMissing()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflowInteractionPort: null);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = request.RunId,
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_interaction_port_unavailable");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
        agent.State.ErrorCode.Should().Be("workflow_interaction_port_unavailable");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowStartFails()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            EmitFrames = false,
            ResultFactory = _ => CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.WorkflowNotFound),
        };
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = BuildWorkflowRequest(),
            RunId = "workflow-draft-run-1",
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_start_failed:WorkflowNotFound");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenFinalizeIsNotTerminal()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            EmitFrames = false,
            ResultFactory = _ => CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                new WorkflowChatRunAcceptedReceipt("workflow-actor-1", "daily-greeting", "workflow-draft-run-1", "msg-1"),
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Unknown,
                    false)),
        };
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = BuildWorkflowRequest(),
            RunId = "workflow-draft-run-1",
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_completion_unknown");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowThrows()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            ThrowOnExecute = true,
        };
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = BuildWorkflowRequest(),
            RunId = "workflow-draft-run-1",
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_draft_run_exception");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public void ReplyRenderer_ShouldRenderWorkflowFailuresAsTerminalReadyText()
    {
        var renderer = new WorkflowDraftRunReplyRenderer();

        var rendered = renderer.Render(new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload
            {
                Message = "boom",
                Code = "bad_input",
            },
        }, "partial");

        rendered.Should().NotBeNull();
        rendered!.IsTerminal.Should().BeTrue();
        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("bad_input");
        rendered.Text.Should().Contain("boom");
    }

    private static NeedsWorkflowDraftRunEvent BuildWorkflowRequest() =>
        new()
        {
            CorrelationId = "msg-1",
            TargetActorId = "conversation-actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-1",
                Content = new MessageContent { Text = "/workflow run daily-greeting" },
            },
            WorkflowSource = new ChannelWorkflowDraftRunSource
            {
                Kind = ChannelWorkflowDraftRunSourceKind.DefinitionActor,
                ScopeId = "scope-1",
                WorkflowId = "daily-greeting",
                WorkflowName = "daily-greeting",
                DefinitionActorId = "workflow-actor-1",
            },
            Prompt = "/workflow run daily-greeting",
            RequestedAtUnixMs = 1,
            RunId = "workflow-draft-run-1",
            ReplyToken = "reply-token-1",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            NyxUserAccessToken = "user-token-1",
            Headers =
            {
                ["registration_id"] = "reg-1",
            },
        };

    private static ChatActivity BuildWorkflowActivity(string? scopeId, string? userToken) =>
        new()
        {
            Id = "msg-1",
            Content = new MessageContent { Text = "/workflow run daily-greeting" },
            TransportExtras = new TransportExtras
            {
                NyxRegistrationScopeId = scopeId ?? string.Empty,
                NyxUserAccessToken = userToken ?? string.Empty,
            },
        };

    private static ScopeWorkflowSummary BuildWorkflowSummary(
        string scopeId,
        string workflowId,
        string actorId = "actor-daily-greeting") =>
        new(
            scopeId,
            workflowId,
            $"Display {workflowId}",
            $"service-key-{workflowId}",
            workflowId,
            actorId,
            "rev-active",
            "deployment-1",
            "active",
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

    private static async Task<ChannelWorkflowDraftRunGAgent> CreateWorkflowDraftRunAgentAsync(
        IActorDispatchPort dispatchPort,
        IWorkflowChatRunInteractionPort? workflowInteractionPort)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChannelWorkflowDraftRunGAgent(
            dispatchPort,
            new WorkflowDraftRunReplyRenderer(),
            NullLogger<ChannelWorkflowDraftRunGAgent>.Instance,
            workflowInteractionPort,
            TimeProvider.System)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChannelWorkflowDraftRunGAgentState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, ["channel-workflow-draft-run:workflow-draft-run-1"]);
        await agent.ActivateAsync();
        return agent;
    }

    private static ChannelSlashCommandRegistry BuildWorkflowSlashRegistry() =>
        new([new ChannelWorkflowDraftRunSlashCommandHandler()]);

    private sealed class RecordingWorkflowChatRunInteractionPort : IWorkflowChatRunInteractionPort
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }
        public bool EmitFrames { get; init; } = true;
        public bool ThrowOnExecute { get; init; }
        public Func<WorkflowChatRunRequest, CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ResultFactory { get; init; } =
            _ => CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                new WorkflowChatRunAcceptedReceipt("workflow-actor-1", "daily-greeting", "workflow-draft-run-1", "msg-1"),
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));

        public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowOnExecute)
                throw new InvalidOperationException("workflow execution failed");

            if (EmitFrames)
            {
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        Delta = "hello",
                    },
                }, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        Delta = " world",
                    },
                }, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    RunFinished = new WorkflowRunFinishedEventPayload(),
                }, ct);
            }

            return ResultFactory(request);
        }
    }

    private sealed class StubScopeWorkflowQueryPort(ScopeWorkflowSummary? workflow) : Aevatar.GAgentService.Abstractions.Ports.IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>(workflow is null ? [] : [workflow]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal)
                ? workflow
                : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.ActorId, actorId, StringComparison.Ordinal)
                ? workflow
                : null);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actor = Substitute.For<IActor>();
            actor.Id.Returns(id ?? Guid.NewGuid().ToString("N"));
            Created.Add(actor.Id);
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<ChannelWorkflowDraftRunGAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            Task.FromResult(0L);
    }
}
