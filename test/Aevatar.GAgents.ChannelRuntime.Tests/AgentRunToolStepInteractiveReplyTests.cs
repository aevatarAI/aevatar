using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
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

// Interactive tools execute only after the actor reconciles the LLM continuation. The
// actor-held capability session keeps the exact final-request objects and the tool step
// opens its own collector scope.
public sealed class AgentRunToolStepInteractiveReplyTests
{
    private const string ReplyArgumentsJson =
        """{"title":"确认部署到 staging?","actions":[{"action_id":"confirm_deploy","label":"确认部署"},{"action_id":"cancel_deploy","label":"取消"}]}""";

    [Fact]
    public async Task BuildLlmStepContinuation_OnRelayTurn_CapturesReplyWithInteractionIntent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var tool = new ReplyWithInteractionTool(collector);
        var executor = CreateExecutor(collector, tool);
        var workItem = BuildLlmStepWorkItem(relay: true);

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);
        var continuation = await executor.BuildToolStepContinuationAsync(
            BuildToolStepWorkItem(workItem, execution.Continuation),
            execution.AuthorizedToolStep,
            CancellationToken.None);

        var toolStepResult = continuation.ToolStepResult;
        toolStepResult.Should().NotBeNull();
        var resultMessage = toolStepResult!.ResultMessages.Should().ContainSingle().Subject;
        resultMessage.Content.Should().NotContain("no_active_interactive_scope");
        resultMessage.Content.Should().Contain("queued");
        toolStepResult.OutboundIntent.Should().NotBeNull();
        toolStepResult.OutboundIntent.Actions.Should()
            .Contain(action => action.ActionId == "confirm_deploy");
    }

    [Fact]
    public async Task BuildLlmStepContinuation_OnNonRelayTurn_KeepsScopeInactive()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var tool = new ReplyWithInteractionTool(collector);
        var executor = CreateExecutor(collector, tool);
        var workItem = BuildLlmStepWorkItem(relay: false);

        var execution = await executor.BuildLlmStepExecutionAsync(workItem, CancellationToken.None);
        var continuation = await executor.BuildToolStepContinuationAsync(
            BuildToolStepWorkItem(workItem, execution.Continuation),
            execution.AuthorizedToolStep,
            CancellationToken.None);

        var toolStepResult = continuation.ToolStepResult;
        toolStepResult.Should().NotBeNull();
        var resultMessage = toolStepResult!.ResultMessages.Should().ContainSingle().Subject;
        resultMessage.Content.Should().Contain("no_active_interactive_scope");
        toolStepResult.OutboundIntent.Should().BeNull();
    }

    private static AgentRunReplyGenerationExecutor CreateExecutor(
        AsyncLocalInteractiveReplyCollector collector,
        IAgentTool tool)
    {
        var tools = new ToolManager();
        tools.Register(tool);
        var provider = new ToolCallProvider(tool.Name);
        var plan = new AgentRunReplyStepPlan(
            CreateStepExecutor(tools, provider),
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

    private static ChatRuntimeStepExecutor CreateStepExecutor(ToolManager tools, ILLMProvider provider)
    {
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new Aevatar.AI.Core.Chat.ChatHistory(),
            toolLoop: new ToolCallLoop(
                tools,
                toolExecutionPort: new ChannelConversationTurnRunnerTests.TestAgentToolExecutionPort()),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [], Tools = tools.GetAll() });
        return runtime.CreateStepExecutor(turnCatalog: null);
    }

    private static AgentRunReplyStepExecutionRequest BuildLlmStepWorkItem(bool relay)
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
            NextStepIndex = 1,
            MaxToolRounds = 4,
        };
        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 1,
            request,
            stepState);
    }

    private static AgentRunReplyStepExecutionRequest BuildToolStepWorkItem(
        AgentRunReplyStepExecutionRequest llmWorkItem,
        AgentRunNextLlmStepRequestedEvent continuation)
    {
        var stepState = llmWorkItem.StepState.Clone();
        stepState.NextStepIndex = continuation.StepIndex;
        stepState.PendingToolCalls.AddRange(continuation.LlmStepResult.ToolCalls.Select(static call => call.Clone()));
        return llmWorkItem with
        {
            StepIndex = continuation.StepIndex,
            StepState = stepState,
        };
    }

    private sealed class ToolCallProvider(string toolName) : ILLMProvider
    {
        public string Name => "interactive-tool-call-provider";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-1",
                    Name = toolName,
                    ArgumentsJson = ReplyArgumentsJson,
                },
            };
            await Task.Yield();
        }
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
            CancellationToken ct,
            AgentTurnToolCatalog? turnCatalog = null) =>
            Task.FromResult(plan);

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepExecutionAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepExecutionAsync only.");
    }
}
