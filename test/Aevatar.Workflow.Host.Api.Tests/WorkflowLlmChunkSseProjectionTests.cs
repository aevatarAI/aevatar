using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowLlmChunkSseProjectionTests
{
    [Fact]
    public async Task WorkflowLlmChunks_ShouldReachSseInOrderBeforeTerminalEvent()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var mapper = new EventEnvelopeToWorkflowRunEventMapper(
        [
            new AITextStreamRunEventEnvelopeMappingHandler(),
            new AIReasoningRunEventEnvelopeMappingHandler(),
            new WorkflowCompletedRunEventEnvelopeMappingHandler(),
        ]);
        var projector = new WorkflowExecutionRunEventProjector(mapper, streamHub);
        var responseBody = new FlushSignalingStream();
        var http = new DefaultHttpContext
        {
            Response = { Body = responseBody },
        };
        await using var writer = new ChatSseResponseWriter(http.Response);
        await using var subscription = await streamHub.SubscribeAsync(
            "workflow-run-1",
            "cmd-stream-1",
            frame => writer.WriteAsync(frame));
        var context = new WorkflowExecutionProjectionContext
        {
            SessionId = "cmd-stream-1",
            RootActorId = "workflow-run-1",
            ProjectionKind = "workflow-execution-session",
        };

        await projector.ProjectAsync(context, WrapCommitted(new WorkflowLlmStreamChunkEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            RoleActorId = "role:assistant",
            DeltaContent = "Hello",
        }, "role:assistant", version: 1));
        await responseBody.WaitForFlushedFrameCountAsync(1, TimeSpan.FromSeconds(2));
        var firstChunkSse = responseBody.SnapshotText();
        ReadSseFrames(firstChunkSse)
            .Count(HasTextMessageContent)
            .Should().Be(1, "the projected SSE payload was {0}", firstChunkSse);

        await projector.ProjectAsync(context, WrapCommitted(new WorkflowLlmStreamChunkEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            RoleActorId = "role:assistant",
            DeltaReasoningContent = "thinking",
        }, "role:assistant", version: 2));
        await responseBody.WaitForFlushedFrameCountAsync(2, TimeSpan.FromSeconds(2));

        await projector.ProjectAsync(context, WrapCommitted(new WorkflowLlmStreamChunkEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            RoleActorId = "role:assistant",
            DeltaContent = " world",
        }, "role:assistant", version: 3));
        await responseBody.WaitForFlushedFrameCountAsync(3, TimeSpan.FromSeconds(2));

        var preTerminalFrames = ReadSseFrames(responseBody.SnapshotText());
        var preTerminalText = preTerminalFrames
            .Where(HasTextMessageContent)
            .Select(frame => frame.GetProperty("textMessageContent"))
            .ToList();
        preTerminalText.Select(frame => frame.GetProperty("messageId").GetString())
            .Should().Equal("msg:session-1", "msg:session-1");
        preTerminalText.Select(frame => frame.GetProperty("delta").GetString())
            .Should().Equal("Hello", " world");
        string.Concat(preTerminalText.Select(frame => frame.GetProperty("delta").GetString()))
            .Should().Be("Hello world");
        preTerminalFrames.Count(IsReasoningFrame).Should().Be(1);
        preTerminalFrames.Count(HasRunFinished).Should().Be(0);

        await projector.ProjectAsync(context, WrapCommitted(new WorkflowCompletedEvent
        {
            RunId = "run-1",
            WorkflowName = "workflow-a",
            Success = true,
            Output = "Hello world",
        }, "workflow-run-1", version: 4));
        await responseBody.WaitForFlushedFrameCountAsync(5, TimeSpan.FromSeconds(2));

        var frames = ReadSseFrames(responseBody.SnapshotText());
        frames.FindIndex(HasRunFinished)
            .Should().BeGreaterThan(frames.FindLastIndex(HasTextMessageContent));
        responseBody.FlushedFrameCount.Should().Be(frames.Count);
    }

    private static EventEnvelope WrapCommitted<T>(T evt, string publisherActorId, long version)
        where T : IMessage
    {
        var eventId = Guid.NewGuid().ToString("N");
        var timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = timestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-stream-1",
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = timestamp.Clone(),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new WorkflowRunState()),
            }),
        };
    }

    private static List<JsonElement> ReadSseFrames(string text)
    {
        return text
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(frame => frame.StartsWith("data: ", StringComparison.Ordinal))
            .Select(frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.Clone())
            .ToList();
    }

    private static bool HasTextMessageContent(JsonElement frame) =>
        frame.TryGetProperty("textMessageContent", out _);

    private static bool HasRunFinished(JsonElement frame) =>
        frame.TryGetProperty("runFinished", out _);

    private static bool IsReasoningFrame(JsonElement frame)
    {
        return frame.TryGetProperty("custom", out var custom) &&
               custom.GetProperty("name").GetString() == "aevatar.llm.reasoning" &&
               custom.GetProperty("payload").GetProperty("delta").GetString() == "thinking";
    }

    private sealed class FlushSignalingStream : MemoryStream
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _dataFrameCount;

        public int FlushedFrameCount { get; private set; }

        public string SnapshotText()
        {
            lock (_gate)
                return System.Text.Encoding.UTF8.GetString(ToArray());
        }

        public Task WaitForFlushedFrameCountAsync(int expectedCount, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (FlushedFrameCount >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expectedCount, waiter));
                return waiter.Task.WaitAsync(timeout);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (buffer.Span.StartsWith("data: "u8))
            {
                lock (_gate)
                    _dataFrameCount++;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                FlushedFrameCount = _dataFrameCount;
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _waiters[index];
                    if (FlushedFrameCount < waiter.Count)
                        continue;

                    _waiters.RemoveAt(index);
                    waiter.Signal.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }
    }
}
