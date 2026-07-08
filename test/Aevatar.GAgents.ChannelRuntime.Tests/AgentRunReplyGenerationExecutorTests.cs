using System.Net.Http;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunReplyGenerationExecutorTests
{
    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasNoSuccessfulMutatingReceipt_ShouldPassReceiptsToCoreConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem();

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_WhenFinalNoToolsHasSuccessfulMutatingReceipt_ShouldSuppressCoreConstraint()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem(
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "definition.update",
            });

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task BuildLlmStepContinuation_ForOwnerFallbackStep_AppendsDegradedToolsDisabledNoticeToRequestCopyOnly()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem();
        workItem.StepState.OwnerFallbackStep = true;
        workItem.StepState.Messages.Insert(
            0,
            AgentRunReplyStepMappers.ToProto(ChatMessage.System("kernel prompt documenting the deployment's tools")));

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.Messages[0].Role.Should().Be("system");
        request.Messages[0].Content.Should().StartWith("kernel prompt documenting the deployment's tools");
        request.Messages[0].Content.Should().Contain("Tools disabled for this turn");
        request.Messages[0].Content.Should().Contain("degraded retry on the bot owner's configuration");
        // The notice lives only on this step's request copy; the persisted step state keeps
        // its original system prompt.
        workItem.StepState.Messages[0].Content.Should().NotContain("Tools disabled for this turn");
    }

    [Fact]
    public async Task BuildLlmStepContinuation_ForRoundExhaustionFinalStep_DoesNotAppendDegradedNotice()
    {
        var provider = new RecordingProvider();
        var executor = CreateExecutor(provider);
        var workItem = BuildFinalNoToolsWorkItem();
        workItem.StepState.Messages.Insert(
            0,
            AgentRunReplyStepMappers.ToProto(ChatMessage.System("kernel prompt documenting the deployment's tools")));

        await executor.BuildLlmStepContinuationAsync(workItem, CancellationToken.None);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Messages[0].Role.Should().Be("system");
        request.Messages[0].Content.Should().NotContain("Tools disabled for this turn");
    }

    [Fact]
    public async Task ExecuteLlmStep_WhenStepPlanBuildingFails_DispatchesStepFailureNotOwnerFallback()
    {
        // Tool discovery runs inside BuildStepPlanAsync. Its failure must never convert the
        // turn into the no-tools owner step, whatever the exception type (funnel B).
        var (dispatchPort, envelopes) = CreateRecordingDispatchPort();
        var executor = new AgentRunReplyGenerationExecutor(
            dispatchPort,
            new ThrowingStepPlanReplyGenerator(new HttpRequestException("tool discovery upstream failed")),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var workItem = BuildOwnerFallbackEligibleWorkItem();

        await executor.ExecuteLlmStepAsync(workItem, CancellationToken.None);

        var envelope = envelopes.Should().ContainSingle().Subject;
        envelope.Payload.Is(AgentRunReplyGenerationFailed.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteLlmStep_WhenProviderRejectsRequest_DispatchesOwnerFallback()
    {
        var (dispatchPort, envelopes) = CreateRecordingDispatchPort();
        var executor = CreateStepExecutor(
            new ThrowingProvider(new NyxIdUpstreamException(
                NyxIdUpstreamFailureKind.RequestRejected,
                status: 400,
                routeName: "nyxid",
                model: "sender-model",
                "Invalid schema for function 'aevatar_observe_run' (HTTP 400).")),
            dispatchPort);
        var workItem = BuildOwnerFallbackEligibleWorkItem();

        await executor.ExecuteLlmStepAsync(workItem, CancellationToken.None);

        var envelope = envelopes.Should().ContainSingle().Subject;
        envelope.Payload.Is(AgentRunOwnerFallbackStepRequested.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteLlmStep_WhenProviderThrowsPlainInvalidOperation_DispatchesStepFailureNotOwnerFallback()
    {
        // Pins the funnel B narrowing: retryability comes from typed exception facts only —
        // a plain InvalidOperationException whose message merely mentions tools/schema/400
        // no longer routes the turn into the no-tools owner step.
        var (dispatchPort, envelopes) = CreateRecordingDispatchPort();
        var executor = CreateStepExecutor(
            new ThrowingProvider(new InvalidOperationException(
                "Invalid schema for function 'aevatar_observe_run': schema must have type 'object' and not have 'oneOf' at the top level (HTTP 400).")),
            dispatchPort);
        var workItem = BuildOwnerFallbackEligibleWorkItem();

        await executor.ExecuteLlmStepAsync(workItem, CancellationToken.None);

        var envelope = envelopes.Should().ContainSingle().Subject;
        envelope.Payload.Is(AgentRunReplyGenerationFailed.Descriptor).Should().BeTrue();
    }

    private static (IActorDispatchPort Port, List<EventEnvelope> Envelopes) CreateRecordingDispatchPort()
    {
        var envelopes = new List<EventEnvelope>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        dispatchPort
            .DispatchAsync(Arg.Any<string>(), Arg.Do<EventEnvelope>(envelopes.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(default(DispatchAdmission)!));
        return (dispatchPort, envelopes);
    }

    private static AgentRunReplyGenerationExecutor CreateExecutor(RecordingProvider provider) =>
        CreateStepExecutor(provider, Substitute.For<IActorDispatchPort>(), disableTools: true);

    private static AgentRunReplyGenerationExecutor CreateStepExecutor(
        ILLMProvider provider,
        IActorDispatchPort dispatchPort,
        bool disableTools = false)
    {
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: new ToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: static () => new LLMRequest { Messages = [] });
        var plan = new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(),
            new Dictionary<string, string>(),
            LLMControlContext.Empty,
            AgentToolExecutionContext.Empty,
            InitialMessages: [],
            MaxToolRounds: 1,
            disableTools);

        return new AgentRunReplyGenerationExecutor(
            dispatchPort,
            new StaticStepPlanReplyGenerator(plan),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
    }

    private static AgentRunReplyStepExecutionRequest BuildFinalNoToolsWorkItem(params AgentToolReceipt[] receipts)
    {
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Content = new MessageContent { Text = "finish" },
            },
        };
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Attempt = 1,
            NextStepIndex = 2,
            Round = 1,
            MaxToolRounds = 1,
            FinalNoToolsStep = true,
        };
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User("hello")));
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.Assistant("partial")));
        stepState.ToolReceipts.AddRange(receipts.Select(receipt => receipt.Clone()));

        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 2,
            request,
            stepState);
    }

    // A bound sender-scoped step: owner fallback is eligible (fallback control present, tools
    // still on, no accumulated text), so only the exception's typed facts decide the outcome.
    private static AgentRunReplyStepExecutionRequest BuildOwnerFallbackEligibleWorkItem()
    {
        var request = new NeedsLlmReplyEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Content = new MessageContent { Text = "hello" },
            },
        };
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Attempt = 1,
            NextStepIndex = 1,
            Round = 0,
            MaxToolRounds = 2,
            FinalNoToolsStep = false,
            OwnerFallbackLlmControl = new LLMControlContextPayload(),
        };
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.System("kernel prompt")));
        stepState.Messages.Add(AgentRunReplyStepMappers.ToProto(ChatMessage.User("hello")));

        return new AgentRunReplyStepExecutionRequest(
            "run-1",
            "channel-agent-run:run-1",
            Attempt: 1,
            StepIndex: 1,
            request,
            stepState);
    }

    private sealed class ThrowingProvider(Exception failure) : ILLMProvider
    {
        public string Name => "throwing-provider";

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            CancellationToken ct = default) =>
            throw failure;
    }

    private sealed class ThrowingStepPlanReplyGenerator(Exception failure) : IAgentRunStepConversationReplyGenerator
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
            throw failure;

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive ExecuteLlmStepAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive ExecuteLlmStepAsync only.");
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string Name => "recording-provider";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = "final" };
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
            CancellationToken ct) =>
            Task.FromResult(plan);

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepContinuationAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Per-step tests drive BuildLlmStepContinuationAsync only.");
    }
}
