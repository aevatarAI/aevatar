using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
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

public sealed class LarkConversationTurnRunnerTests
{
    [Fact]
    public async Task RunInboundAsync_ShouldGenerateAndSendReply_ForNormalMessage()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var replyGenerator = new StubReplyGenerator("reply-1");
        var runner = CreateRunner(registrationQueryPort, adapter, replyGenerator: replyGenerator);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                ConversationScope.Group,
                "oc_group_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    ReplyAccessToken = "relay-token-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-1");
        result.OutboundDelivery?.ReplyMessageId.Should().Be("relay-msg-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("reply-1");
        adapter.Replies[0].Inbound.ConversationId.Should().Be("oc_group_chat_1");
        adapter.Replies[0].Inbound.OutboundDelivery?.ReplyAccessToken.Should().Be("relay-token-1");
        replyGenerator.GeneratedActivities.Should().ContainSingle(activity => activity.Id == "msg-1");
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
        var replyGenerator = new StubReplyGenerator("llm-fallback-should-not-fire");
        var runner = CreateRunner(registrationQueryPort, adapter, replyGenerator: replyGenerator);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily-report alice",
                "msg-slash-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-slash-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("Create daily report agent failed");
        adapter.Replies[0].ReplyText.Should().Contain("No NyxID access token available");
        replyGenerator.GeneratedActivities.Should().BeEmpty(
            because: "deterministic slash flow must not fall through to the LLM reply generator");
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

    private static LarkConversationTurnRunner CreateRunner(
        IChannelBotRegistrationQueryPort registrationQueryPort,
        RecordingPlatformAdapter adapter,
        IServiceProvider? services = null,
        IConversationReplyGenerator? replyGenerator = null)
    {
        services ??= new ServiceCollection().BuildServiceProvider();
        return new LarkConversationTurnRunner(
            services,
            registrationQueryPort,
            [adapter],
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://example.com" }),
            replyGenerator ?? new StubReplyGenerator(),
            NullLogger<LarkConversationTurnRunner>.Instance);
    }

    private static ChatActivity BuildInboundActivity(
        string text,
        string messageId,
        ConversationScope scope = ConversationScope.Group,
        string? partition = "oc_group_chat_1",
        OutboundDeliveryContext? outboundDelivery = null)
    {
        return new ChatActivity
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                scope,
                partition,
                scope == ConversationScope.Group ? "group" : "dm",
                scope == ConversationScope.Group ? "oc_group_chat_1" : "ou_user_1"),
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
        };
    }

    private static IChannelBotRegistrationQueryPort BuildRegistrationQueryPort()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(registration.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        return queryPort;
    }

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

    private sealed class StubReplyGenerator(string? reply = "unused") : IConversationReplyGenerator
    {
        public List<ChatActivity> GeneratedActivities { get; } = [];

        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct)
        {
            GeneratedActivities.Add(activity);
            return Task.FromResult(reply);
        }
    }

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
}
