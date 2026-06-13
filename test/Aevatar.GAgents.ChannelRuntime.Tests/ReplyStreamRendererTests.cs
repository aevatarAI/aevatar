using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ReplyStreamRendererTests
{
    [Fact]
    public void NyxRelayTextRenderer_CreatesTypedStepPayload()
    {
        var renderer = new NyxRelayTextReplyStreamRenderer(
            new RecordingTurnRunner(),
            NullLogger<NyxRelayTextReplyStreamRenderer>.Instance);

        var step = renderer.CreateStep(new NyxRelayTextOperationStepInput(
            NyxRelayTextOperationKind.Interim,
            CreateTextChunk(),
            "corr-1",
            "platform-1",
            "cmd-1",
            "final",
            "last",
            2,
            3,
            4));

        step.OperationId.Should().Be("corr-1:Interim:3:4");
        step.OperationName.Should().Be("nyx-relay-text-Interim");
        step.CorrelationId.Should().Be("corr-1");
        step.LeaseEpoch.Should().Be(4);
        step.NyxRelayText.Operation.Should().Be(NyxRelayTextOperationKind.Interim);
        step.NyxRelayText.Chunk.Should().NotBeSameAs(CreateTextChunk());
        step.PayloadCase.Should().Be(ReplyOperationStepEvent.PayloadOneofCase.NyxRelayText);
    }

    [Fact]
    public async Task NyxRelayTextRenderer_ExecutesMatchingStepAndDispatchesCompletion()
    {
        var runner = new RecordingTurnRunner();
        var renderer = new NyxRelayTextReplyStreamRenderer(
            runner,
            NullLogger<NyxRelayTextReplyStreamRenderer>.Instance);
        var context = new RecordingReplyOperationContext(matchesNyx: true, matchesLark: false);
        var step = renderer.CreateStep(new NyxRelayTextOperationStepInput(
            NyxRelayTextOperationKind.Final,
            CreateTextChunk(),
            "corr-1",
            "platform-1",
            "cmd-1",
            "final text",
            "last text",
            1,
            2,
            3));

        await renderer.ExecuteAsync(context, step, CancellationToken.None);

        runner.StreamChunks.Should().ContainSingle();
        context.Dispatched.Should().ContainSingle();
        var completed = context.Dispatched[0].Event.Should().BeOfType<NyxRelayTextOperationCompletedEvent>().Subject;
        completed.Operation.Should().Be(NyxRelayTextOperationKind.Final);
        completed.OperationId.Should().Be("corr-1:Final:2:3");
        completed.RawResult.PlatformMessageId.Should().Be("platform-result");
    }

    [Fact]
    public async Task NyxRelayTextRenderer_IgnoresStaleStepWithoutRunningIo()
    {
        var runner = new RecordingTurnRunner();
        var renderer = new NyxRelayTextReplyStreamRenderer(
            runner,
            NullLogger<NyxRelayTextReplyStreamRenderer>.Instance);
        var context = new RecordingReplyOperationContext(matchesNyx: false, matchesLark: false);
        var step = renderer.CreateStep(new NyxRelayTextOperationStepInput(
            NyxRelayTextOperationKind.Interim,
            CreateTextChunk(),
            "corr-1",
            null,
            null,
            null,
            null,
            0,
            1,
            1));

        await renderer.ExecuteAsync(context, step, CancellationToken.None);

        runner.StreamChunks.Should().BeEmpty();
        context.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public void LarkCardRenderer_CreatesFinalizeStepWithTypedPayload()
    {
        var renderer = new LarkCardReplyStreamRenderer(
            new RecordingCardTurnRunner(),
            NullLogger<LarkCardReplyStreamRenderer>.Instance);

        var step = renderer.CreateFinalizeStep(new LarkCardFinalizeOperationStepInput(
            CreateActivity(),
            "corr-card",
            "cmd-card",
            "final text",
            "last text",
            "card-1",
            "message-1",
            "streaming_main",
            FinalDiffers: true,
            [new ConversationHistoryEntry { Role = "assistant", Content = "final text" }],
            Sequence: 7,
            Generation: 8));

        step.OperationId.Should().Be("corr-card:Finalize:7:8");
        step.OperationName.Should().Be("lark-card-finalize");
        step.PayloadCase.Should().Be(ReplyOperationStepEvent.PayloadOneofCase.LarkCard);
        step.LarkCard.Operation.Should().Be(LarkCardOperationPhase.Finalize);
        step.LarkCard.AppendedHistory.Should().ContainSingle();
        step.LarkCard.Activity.Should().NotBeSameAs(CreateActivity());
    }

    [Fact]
    public async Task LarkCardRenderer_ExecutesFinalizeAndSanitizesActivityInCompletion()
    {
        var runner = new RecordingCardTurnRunner();
        var renderer = new LarkCardReplyStreamRenderer(
            runner,
            NullLogger<LarkCardReplyStreamRenderer>.Instance);
        var context = new RecordingReplyOperationContext(matchesNyx: false, matchesLark: true);
        var step = renderer.CreateFinalizeStep(new LarkCardFinalizeOperationStepInput(
            CreateActivity(),
            "corr-card",
            "cmd-card",
            "final text",
            "last text",
            "card-1",
            "message-1",
            "streaming_main",
            FinalDiffers: true,
            [],
            Sequence: 7,
            Generation: 8));

        await renderer.ExecuteAsync(context, step, CancellationToken.None);

        runner.Finalizes.Should().ContainSingle();
        context.Dispatched.Should().ContainSingle();
        var completed = context.Dispatched[0].Event.Should().BeOfType<LarkCardOperationCompletedEvent>().Subject;
        completed.Operation.Should().Be(LarkCardOperationPhase.Finalize);
        completed.OperationId.Should().Be("corr-card:Finalize:7:8");
        completed.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
    }

    private static LlmReplyStreamChunkEvent CreateTextChunk() =>
        new()
        {
            CorrelationId = "corr-1",
            RegistrationId = "reg-1",
            Activity = CreateActivity(),
            AccumulatedText = "hello",
            ChunkAtUnixMs = 10,
            ReplyToken = "reply-token",
            ReplyTokenExpiresAtUnixMs = 20,
        };

    private static ChatActivity CreateActivity() =>
        new()
        {
            Id = "activity-1",
            Type = ActivityType.Message,
            ChannelId = new ChannelId { Value = "lark" },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "lark" },
                CanonicalKey = "conv:lark:1",
            },
            TransportExtras = new TransportExtras
            {
                NyxUserAccessToken = "runtime-user-token",
            },
        };

    private sealed class RecordingTurnRunner : IConversationTurnRunner
    {
        public List<LlmReplyStreamChunkEvent> StreamChunks { get; } = [];

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", activity.Id));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", reply.CorrelationId));

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            StreamChunks.Add(chunk.Clone());
            return Task.FromResult(ConversationStreamChunkResult.Succeeded("platform-result"));
        }
    }

    private sealed class RecordingCardTurnRunner : IConversationCardTurnRunner
    {
        public List<ChatActivity> Finalizes { get; } = [];

        public Task<ConversationCardCreateResult> RunCardCreateAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string streamingElementId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationCardCreateResult.Succeeded("card-1", "message-1"));

        public Task<ConversationCardStreamResult> RunCardStreamAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string cardId,
            string elementId,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationCardStreamResult.Succeeded());

        public Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
            ChatActivity referenceActivity,
            string cardId,
            string elementId,
            string finalText,
            bool finalTextDiffersFromLastFlushed,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            Finalizes.Add(referenceActivity.Clone());
            return Task.FromResult(ConversationCardFinalizeResult.Succeeded());
        }
    }

    private sealed class RecordingReplyOperationContext(
        bool matchesNyx,
        bool matchesLark) : IReplyOperationActorContext
    {
        public List<(IMessage Event, string CorrelationId, string OperationName)> Dispatched { get; } = [];

        public bool MatchesNyxRelayTextInFlight(
            string correlationId,
            NyxRelayTextOperationKind operation,
            long sequence,
            long generation) =>
            matchesNyx;

        public bool MatchesLarkCardInFlight(
            string correlationId,
            LarkCardOperationPhase operation,
            long sequence,
            long generation,
            string? cardId) =>
            matchesLark;

        public ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(
            string? correlationId,
            ChatActivity? activity,
            string? replyToken,
            long replyTokenExpiresAtUnixMs) =>
            ConversationTurnRuntimeContext.Empty;

        public void RestoreRuntimeTransportCredentials(
            ChatActivity? activity,
            ConversationTurnRuntimeContext runtimeContext)
        {
        }

        public Task DispatchReplyOperationCompletionAsync(
            IMessage evt,
            string correlationId,
            string operationName,
            CancellationToken ct)
        {
            Dispatched.Add((evt, correlationId, operationName));
            return Task.CompletedTask;
        }
    }
}
