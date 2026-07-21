using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

// Regression for the relay interactive-reply scope gap: reply_with_interaction executes
// during the TOOL step (BuildToolStepContinuationAsync), but only the LLM step opened an
// IInteractiveReplyCollector scope. The AsyncLocal scope does not survive the actor
// continuation hop between steps, so the tool always answered no_active_interactive_scope
// and every relay card (DM and group chat alike) degraded to plain text. These tests pin:
//   1. the tool step opens a collector scope on relay turns and returns the captured
//      intent as a typed fact on AgentRunToolStepResult.outbound_intent;
//   2. non-relay turns still expose no scope (console turns stay text-only).
public sealed class AgentRunToolStepInteractiveReplyTests
{
    private const string ReplyArgumentsJson =
        """{"title":"确认部署到 staging?","actions":[{"action_id":"confirm_deploy","label":"确认部署"},{"action_id":"cancel_deploy","label":"取消"}]}""";

    [Fact]
    public async Task BuildToolStepContinuation_OnRelayTurn_CapturesReplyWithInteractionIntent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var executor = CreateExecutor(collector);
        var workItem = BuildToolStepWorkItem(relay: true);

        var continuation = await executor.BuildToolStepContinuationAsync(workItem, CancellationToken.None);

        var resultMessage = continuation.ToolStepResult.ResultMessages.Should().ContainSingle().Subject;
        resultMessage.Content.Should().NotContain("no_active_interactive_scope");
        resultMessage.Content.Should().Contain("queued");
        continuation.ToolStepResult.OutboundIntent.Should().NotBeNull();
        continuation.ToolStepResult.OutboundIntent.Actions.Should()
            .Contain(action => action.ActionId == "confirm_deploy");
    }

    [Fact]
    public async Task BuildToolStepContinuation_OnNonRelayTurn_KeepsScopeInactive()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var executor = CreateExecutor(collector);
        var workItem = BuildToolStepWorkItem(relay: false);

        var continuation = await executor.BuildToolStepContinuationAsync(workItem, CancellationToken.None);

        var resultMessage = continuation.ToolStepResult.ResultMessages.Should().ContainSingle().Subject;
        resultMessage.Content.Should().Contain("no_active_interactive_scope");
        continuation.ToolStepResult.OutboundIntent.Should().BeNull();
    }

    private static AgentRunReplyGenerationExecutor CreateExecutor(AsyncLocalInteractiveReplyCollector collector)
    {
        var tools = new ToolManager();
        tools.Register(new ReplyWithInteractionTool(collector));
        var plan = new AgentRunReplyStepPlan(
            CreateStepExecutor(tools),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 4);
        return new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new StaticStepPlanReplyGenerator(plan),
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
    }

    private static ChatRuntimeStepExecutor CreateStepExecutor(ToolManager tools)
    {
        var runtime = new ChatRuntime(
            providerFactory: static () => throw new InvalidOperationException("Tool step must not invoke the LLM provider."),
            history: new Aevatar.AI.Core.Chat.ChatHistory(),
            toolLoop: new ToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() });
        return runtime.CreateStepExecutor(turnCatalog: null);
    }

    private static AgentRunReplyStepExecutionRequest BuildToolStepWorkItem(bool relay)
    {
        var activity = new ChatActivity
        {
            Id = "msg-1",
            Content = new MessageContent { Text = "/deploy" },
        };
        if (relay)
        {
            activity.OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-1",
            };
        }

        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            RunId = "run-1",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = activity,
        };

        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Attempt = 1,
            NextStepIndex = 2,
            MaxToolRounds = 4,
            PendingToolCalls =
            {
                new AgentRunToolCall
                {
                    Id = "call-1",
                    Name = "reply_with_interaction",
                    ArgumentsJson = ReplyArgumentsJson,
                },
            },
        };

        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 2,
            request,
            stepState);
    }

    private sealed class StaticStepPlanReplyGenerator(AgentRunReplyStepPlan plan) : IAgentRunStepConversationReplyGenerator
    {
        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IReadOnlyList<ConversationHistoryEntry>? priorHistory,
            ChatAttachmentInputContext? attachmentContext,
            bool forceDisableTools,
            CancellationToken ct) =>
            Task.FromResult(plan);

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildToolStepContinuationAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildToolStepContinuationAsync only.");
    }
}
