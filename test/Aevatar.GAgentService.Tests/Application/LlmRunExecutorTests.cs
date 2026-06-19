using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmRunExecutorTests
{
    [Fact]
    public async Task DispatchingSink_ShouldPublishTypedRecorderFactsWithRunIdentity()
    {
        var dispatch = new LlmRunAcceptanceHarness.RecordingActorDispatchPort();
        var sink = new LlmRunAcceptanceHarness.DispatchingLlmRunSink(
            LlmRunAcceptanceHarness.ActorId,
            dispatch);

        await sink.RecordStreamChunkObservedAsync(LlmRunAcceptanceHarness.Chunk("hello"));
        await sink.RecordRunCompletedAsync(LlmRunAcceptanceHarness.Completed());

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls.Should().OnlyContain(call => call.ActorId == LlmRunAcceptanceHarness.ActorId);
        dispatch.Calls[0].Envelope.Payload!.Unpack<LlmStreamChunkObserved>().Should().Match<LlmStreamChunkObserved>(
            observed => observed.ResponseId == LlmRunAcceptanceHarness.ResponseId &&
                        observed.RunId == LlmRunAcceptanceHarness.RunId &&
                        observed.DeltaText == "hello");
        dispatch.Calls[1].Envelope.Payload!.Unpack<LlmRunCompleted>().Should().Match<LlmRunCompleted>(
            completed => completed.ResponseId == LlmRunAcceptanceHarness.ResponseId &&
                         completed.RunId == LlmRunAcceptanceHarness.RunId &&
                         completed.OutputText == "hello world");
    }

    [Fact]
    public async Task RunAsync_ShouldContinuouslyConsumeProviderStreamAfterActorTurnAcceptance()
    {
        var streamEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new LlmRunAcceptanceHarness.GatedLlmProviderFactory(
            streamEntered,
            releaseStream,
            [
                new LLMStreamChunk { DeltaContent = "hello " },
                new LLMStreamChunk
                {
                    DeltaContent = "world",
                    Usage = new TokenUsage(1, 2, 3),
                    IsLast = true,
                },
            ]);
        var dispatch = new LlmRunAcceptanceHarness.RecordingActorDispatchPort();
        var sink = new LlmRunAcceptanceHarness.DispatchingLlmRunSink(
            LlmRunAcceptanceHarness.ActorId,
            dispatch);
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);

        var runTask = core.RunAsync(
            new LlmRunCoreRequest(LlmRunAcceptanceHarness.BuildRunRequest(), LlmRunAcceptanceHarness.RunId, "ApiKey"),
            sink);
        await streamEntered.Task;
        dispatch.Calls.Should().BeEmpty();

        releaseStream.SetResult();
        await runTask;

        dispatch.Calls.Should().HaveCount(3);
        dispatch.Calls.Select(call => call.Envelope.Payload!.TypeUrl)
            .Should()
            .Equal(
                "type.googleapis.com/aevatar.gagentservice.LlmStreamChunkObserved",
                "type.googleapis.com/aevatar.gagentservice.LlmStreamChunkObserved",
                "type.googleapis.com/aevatar.gagentservice.LlmRunCompleted");
        dispatch.Calls[2].Envelope.Payload!.Unpack<LlmRunCompleted>().Usage!.TotalTokens.Should().Be(3);
    }

    [Fact]
    public async Task DispatchingSink_ShouldDispatchCancelledFactEvenWhenCallerTokenIsCancelled()
    {
        var dispatch = new LlmRunAcceptanceHarness.RecordingActorDispatchPort();
        var sink = new LlmRunAcceptanceHarness.DispatchingLlmRunSink(
            LlmRunAcceptanceHarness.ActorId,
            dispatch);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sink.RecordRunCancelledAsync(LlmRunAcceptanceHarness.Cancelled(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        dispatch.Calls.Should().BeEmpty();

        await sink.RecordRunCancelledAsync(LlmRunAcceptanceHarness.Cancelled(), CancellationToken.None);

        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].Envelope.Payload!.Unpack<LlmRunCancelled>().RunId
            .Should()
            .Be(LlmRunAcceptanceHarness.RunId);
    }
}
