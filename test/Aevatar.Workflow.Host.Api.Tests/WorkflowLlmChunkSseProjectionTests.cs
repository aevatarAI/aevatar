using System.Reflection;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Integration.AI;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        await using var projectionSubscription = await streams.GetStream("workflow-run-1")
            .SubscribeAsync<EventEnvelope>(envelope => projector.ProjectAsync(context, envelope).AsTask());
        await using var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var llmProvider = new StreamingLlmProvider();
        var roleAgent = new WorkflowRoleGAgent(UnexpectedAgentToolExecutionPort.Instance, llmProvider)
        {
            Services = services,
            EventPublisher = new LocalActorPublisher(
                "role:assistant",
                () => "workflow-run-1",
                () => 0,
                streams),
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        SetAgentId(roleAgent, "role:assistant");
        await roleAgent.ActivateAsync();
        await roleAgent.HandleWorkflowRoleInitialize(new WorkflowRoleInitializeEvent
        {
            RoleId = "assistant",
            RoleName = "Assistant",
            ProviderName = "mock",
            SystemPrompt = "workflow role",
        });

        var execution = roleAgent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            Prompt = "hello",
        });
        await responseBody.WaitForSnapshotAsync(
            text => ReadSseFrames(text).Count(HasTextMessageContent) >= 1,
            TimeSpan.FromSeconds(2));
        var firstChunkSse = responseBody.SnapshotText();
        ReadSseFrames(firstChunkSse)
            .Count(HasTextMessageContent)
            .Should().Be(1, "the projected SSE payload was {0}", firstChunkSse);

        llmProvider.ReleaseRemainingChunks();
        await execution;
        await responseBody.WaitForSnapshotAsync(
            text => ReadSseFrames(text).Count(HasTextMessageContent) >= 2,
            TimeSpan.FromSeconds(2));

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

        var runPublisher = new LocalActorPublisher(
            "workflow-run-1",
            () => null,
            () => 0,
            streams);
        await runPublisher.PublishAsync(new WorkflowCompletedEvent
        {
            RunId = "run-1",
            WorkflowName = "workflow-a",
            Success = true,
            Output = "Hello world",
        }, TopologyAudience.Self);
        await responseBody.WaitForSnapshotAsync(
            text => ReadSseFrames(text).Any(HasRunFinished),
            TimeSpan.FromSeconds(2));

        var frames = ReadSseFrames(responseBody.SnapshotText());
        frames.FindIndex(HasRunFinished)
            .Should().BeGreaterThan(frames.FindLastIndex(HasTextMessageContent));
        responseBody.FlushedFrameCount.Should().Be(frames.Count);
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
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

    private sealed class StreamingLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _remainingChunksReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "mock";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public void ReleaseRemainingChunks() => _remainingChunksReleased.TrySetResult();

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "Hello" };
            await _remainingChunksReleased.Task.WaitAsync(ct);
            yield return new LLMStreamChunk { DeltaReasoningContent = "thinking" };
            yield return new LLMStreamChunk { DeltaContent = " world" };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            await Task.CompletedTask;
        }
    }

    private sealed class UnexpectedAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public static UnexpectedAgentToolExecutionPort Instance { get; } = new();

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException(
                $"Tool '{request.Tool.Name}' must not execute in workflow SSE projection tests.");
    }

    private sealed class FlushSignalingStream : MemoryStream
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private readonly List<(Func<string, bool> Predicate, TaskCompletionSource Signal)> _snapshotWaiters = [];
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

        public Task WaitForSnapshotAsync(Func<string, bool> predicate, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (predicate(SnapshotTextUnsafe()))
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _snapshotWaiters.Add((predicate, waiter));
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

                var text = SnapshotTextUnsafe();
                for (var index = _snapshotWaiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _snapshotWaiters[index];
                    if (!waiter.Predicate(text))
                        continue;

                    _snapshotWaiters.RemoveAt(index);
                    waiter.Signal.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }

        private string SnapshotTextUnsafe() => System.Text.Encoding.UTF8.GetString(ToArray());
    }
}
