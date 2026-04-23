using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelConversationTurnRunnerTests
{
    [Fact]
    public async Task RunInboundAsync_ShouldRequestDeferredLlmReply_ForNormalMessage()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-1", ConversationScope.Group, "oc_group_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.CorrelationId.Should().Be("msg-1");
        result.LlmReplyRequest.TargetActorId.Should().Be("channel-conversation:lark:group:oc_group_chat_1");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.ChatType].Should().Be("group");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldIncludePlatformMessageIdInLlmMetadata_WhenAvailable()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_123",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.PlatformMessageId].Should().Be("om_123");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSendImmediateLarkReaction_WhenRelayTurnProvidesPlatformMessageId()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = "user-token-1",
                    NyxPlatformMessageId = "om_123",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        nyxHandler.Requests.Should().ContainSingle();
        nyxHandler.Requests[0].Path.Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reactions");
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer user-token-1");
        nyxHandler.Requests[0].Body.Should().Contain("\"emoji_type\":\"OK\"");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSkipImmediateLarkReaction_WhenPlatformMessageIdIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = "user-token-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldResolveRegistrationByNyxAgentApiKeyId_WhenBotIdDoesNotMatch()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("missing-reg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(BuildRegistrationEntry()));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-1",
                botId: "missing-reg",
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxMessageId = "nyx-msg-1",
                    NyxPlatform = "lark",
                    NyxConversationId = "nyx-conv-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.RegistrationId.Should().Be("reg-1");
        result.LlmReplyRequest.Activity.TransportExtras?.NyxAgentApiKeyId.Should().Be("nyx-key-1");
        await registrationByNyxIdentityPort.Received(1)
            .GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>());
        await registrationQueryPort.DidNotReceive()
            .GetAsync("missing-reg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldMapChannelScopeChatTypeExhaustively()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("announce", "msg-channel-1", ConversationScope.Channel, "oc_channel_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.ChatType].Should().Be("channel");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldReturnTransientFailure_WhenWorkflowResumeServiceIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1", "msg-resume-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("workflow_resume_service_unavailable");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldReturnWorkflowReceipt_WhenResumeCommandIsAccepted()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-1", "corr-1")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1 comment='ship it'", "msg-resume-2"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("workflow-resume:cmd-1");
        adapter.Replies.Should().BeEmpty();
        dispatchService.Commands.Should().ContainSingle();
        dispatchService.Commands[0].ActorId.Should().Be("actor-1");
        dispatchService.Commands[0].RunId.Should().Be("run-1");
        dispatchService.Commands[0].StepId.Should().Be("step-1");
        dispatchService.Commands[0].Approved.Should().BeTrue();
        dispatchService.Commands[0].Feedback.Should().Be("ship it");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteCardActionResume_WhenCardPayloadContainsWorkflowIds()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-card-1", "corr-card-1")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildCardActionActivity(
                "evt-card-1",
                ("actor_id", "actor-1"),
                ("run_id", "run-1"),
                ("step_id", "approval-1"),
                ("approved", "false"),
                ("user_input", "Need stronger hook")),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("workflow-resume:cmd-card-1");
        adapter.Replies.Should().BeEmpty();
        dispatchService.Commands.Should().ContainSingle();
        dispatchService.Commands[0].ActorId.Should().Be("actor-1");
        dispatchService.Commands[0].RunId.Should().Be("run-1");
        dispatchService.Commands[0].StepId.Should().Be("approval-1");
        dispatchService.Commands[0].Approved.Should().BeFalse();
        dispatchService.Commands[0].Feedback.Should().Be("Need stronger hook");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldMapWorkflowResumeValidationErrors()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " ")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1", "msg-resume-3"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_step_id");
        result.ErrorSummary.Should().Contain("stepId");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteSlashCommand_WhenRegistrationHasNoRelayApiKey()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily alice",
                "msg-slash-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-slash-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("Create daily report agent failed");
        adapter.Replies[0].ReplyText.Should().Contain("No NyxID access token available");
    }

    [Theory]
    [InlineData("/daily_report")]
    [InlineData("/foobar")]
    [InlineData("/")]
    public async Task RunInboundAsync_ShouldShortCircuitUnknownSlashCommand_WithUsage(string command)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(command, "msg-unknown", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain($"Unknown command: {command}");
        adapter.Replies[0].ReplyText.Should().Contain("Supported commands:");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldSendRelayReply_WhenReadyEventArrives()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                ReplyAccessToken = "relay-token-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
            });

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-relay-1",
            RegistrationId = "reg-1",
            SourceActorId = "llm-worker-1",
            Activity = activity,
            Outbound = new MessageContent { Text = "relay reply" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 42,
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-1");
        result.OutboundDelivery?.ReplyMessageId.Should().Be("relay-msg-1");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-1\"");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"relay reply\"");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldDispatchInteractiveRelayReply_WhenOutboundContainsActions()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var interactiveDispatcher = Substitute.For<IInteractiveReplyDispatcher>();
        interactiveDispatcher.DispatchAsync(
                Arg.Any<ChannelId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MessageContent>(),
                Arg.Any<ComposeContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InteractiveReplyDispatchResult(
                Succeeded: true,
                MessageId: "reply-card-1",
                PlatformMessageId: "platform-card-1",
                Capability: ComposeCapability.Exact,
                FellBackToText: false,
                Detail: null)));
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            interactiveReplyDispatcher: interactiveDispatcher);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-card-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                ReplyAccessToken = "relay-token-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
            });
        var outbound = new MessageContent
        {
            Text = "Choose one",
        };
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-relay-card-1",
            RegistrationId = "reg-1",
            SourceActorId = "llm-worker-1",
            Activity = activity,
            Outbound = outbound,
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 42,
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-card-1");
        result.Outbound.Actions.Should().ContainSingle();
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
        await interactiveDispatcher.Received(1).DispatchAsync(
            Arg.Is<ChannelId>(channel => channel.Value == "lark"),
            "relay-msg-1",
            "relay-token-1",
            Arg.Is<MessageContent>(message => message.Text == "Choose one" && message.Actions.Count == 1),
            Arg.Any<ComposeContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunContinueAsync_DirectMessageWithoutPartition_ReturnsPermanentFailure()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                partition: null,
                "dm",
                "user-open-id"),
            Payload = new MessageContent { Text = "hello" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("conversation_not_found");
        result.ErrorSummary.Should().Contain("routing target");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunContinueAsync_GroupWithoutPartition_UsesCanonicalFallback()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-2",
            CorrelationId = "corr-2",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                partition: null,
                "group",
                "oc_group_chat_1"),
            Payload = new MessageContent { Text = "hello group" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.ConversationId.Should().Be("oc_group_chat_1");
        adapter.Replies[0].Inbound.ChatType.Should().Be("group");
    }

    private static ChannelConversationTurnRunner CreateRunner(
        IChannelBotRegistrationQueryPort registrationQueryPort,
        RecordingPlatformAdapter adapter,
        IServiceProvider? services = null,
        IChannelBotRegistrationQueryByNyxIdentityPort? registrationQueryByNyxIdentityPort = null,
        RecordingJsonHandler? relayHandler = null,
        RecordingJsonHandler? nyxHandler = null,
        IInteractiveReplyDispatcher? interactiveReplyDispatcher = null)
    {
        services ??= new ServiceCollection().BuildServiceProvider();
        relayHandler ??= new RecordingJsonHandler("""{"message_id":"relay-reply"}""");
        nyxHandler ??= new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var relayClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://example.com" },
            new HttpClient(relayHandler)
            {
                BaseAddress = new Uri("https://example.com"),
            },
            NullLogger<NyxIdApiClient>.Instance);
        var relayOutboundPort = new NyxIdRelayOutboundPort(
            relayClient,
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new RelayStubComposer("lark")]);

        return new ChannelConversationTurnRunner(
            services,
            registrationQueryPort,
            registrationQueryByNyxIdentityPort,
            [adapter],
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://example.com" },
                new HttpClient(nyxHandler)
                {
                    BaseAddress = new Uri("https://example.com"),
                }),
            relayOutboundPort,
            interactiveReplyDispatcher,
            NullLogger<ChannelConversationTurnRunner>.Instance);
    }

    private static ChatActivity BuildInboundActivity(
        string text,
        string messageId,
        ConversationScope scope = ConversationScope.Group,
        string? partition = "oc_group_chat_1",
        OutboundDeliveryContext? outboundDelivery = null,
        TransportExtras? transportExtras = null,
        string botId = "reg-1")
    {
        return new ChatActivity
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From(botId),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From(botId),
                scope,
                partition,
                scope switch
                {
                    ConversationScope.Group => "group",
                    ConversationScope.Channel => "channel",
                    _ => "dm",
                },
                scope switch
                {
                    ConversationScope.Group => "oc_group_chat_1",
                    ConversationScope.Channel => "oc_channel_1",
                    _ => "ou_user_1",
                }),
            From = new ParticipantRef
            {
                CanonicalId = "ou_user_1",
                DisplayName = "User One",
            },
            Content = new MessageContent
            {
                Text = text,
            },
            OutboundDelivery = outboundDelivery?.Clone(),
            TransportExtras = transportExtras?.Clone(),
        };
    }

    private static ChatActivity BuildCardActionActivity(string eventId, params (string Key, string Value)[] fields)
    {
        var cardAction = new CardActionSubmission
        {
            SourceMessageId = "om-card-1",
        };

        foreach (var (key, value) in fields)
            cardAction.FormFields[key] = value;

        return new ChatActivity
        {
            Id = eventId,
            Type = ActivityType.CardAction,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                partition: "oc_chat_1",
                "dm",
                "ou_user_1"),
            From = new ParticipantRef
            {
                CanonicalId = "ou_user_1",
                DisplayName = "User One",
            },
            Content = new MessageContent
            {
                CardAction = cardAction,
            },
        };
    }

    private static IChannelBotRegistrationQueryPort BuildRegistrationQueryPort()
    {
        var registration = BuildRegistrationEntry();
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(registration.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        return queryPort;
    }

    private static ChannelBotRegistrationEntry BuildRegistrationEntry(string id = "reg-1") =>
        new()
        {
            Id = id,
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private sealed class RecordingPlatformAdapter : IPlatformAdapter
    {
        public string Platform => "lark";

        public List<(string ReplyText, InboundMessage Inbound)> Replies { get; } = [];

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            Replies.Add((replyText, inbound));
            return Task.FromResult(new PlatformReplyDeliveryResult(true, "ok"));
        }
    }

    private sealed class RelayStubComposer(string platform) : IMessageComposer<RelayStubPayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public RelayStubPayload Compose(MessageContent intent, ComposeContext context) =>
            new(intent.Text ?? string.Empty);

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => ComposeCapability.Exact;
    }

    private sealed record RelayStubPayload(string PlainText) : IPlainTextComposedMessage;

    private sealed class RecordingWorkflowResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public required CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> Result { get; init; }

        public List<WorkflowResumeCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingJsonHandler(string body) : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
