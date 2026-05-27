using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI.Tests;

public sealed class WorkflowAiLlmInvocationPortTests
{
    [Fact]
    public async Task InvokeAsync_ShouldMapIntentToProviderCall_AndYieldTypedStreamEvents()
    {
        var provider = new RecordingProvider(
        [
            new LLMStreamChunk { DeltaReasoningContent = "thinking" },
            new LLMStreamChunk { DeltaContent = "hel" },
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-1",
                    Name = "lookup",
                    ArgumentsJson = """{"q":"a"}""",
                },
            },
            new LLMStreamChunk { DeltaContent = "lo", IsLast = true, FinishReason = "stop" },
        ]);
        var port = new WorkflowAiLlmInvocationPort(
            new RecordingProviderFactory(provider),
            NullLogger<WorkflowAiLlmInvocationPort>.Instance);

        var events = await CollectAsync(port.InvokeAsync(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            TargetRole = "writer",
            ProviderName = " test-provider ",
            Prompt = "user prompt",
            SystemPrompt = "system prompt",
            Model = "model-a",
            ModelOverride = "model-b",
            Temperature = 0.2,
            MaxTokens = 128,
            MaxToolRounds = 2,
            MaxToolRoundsOverride = 4,
            UserMemoryPrompt = "remember",
            Annotations = { ["trace"] = "abc" },
            InputParts =
            {
                new WorkflowChatContentPart
                {
                    Kind = WorkflowChatContentPartKind.Text,
                    Text = "part-text",
                },
            },
        }));

        provider.Calls.Should().ContainSingle();
        var request = provider.Calls[0];
        request.Model.Should().Be("model-b");
        request.Temperature.Should().Be(0.2);
        request.MaxTokens.Should().Be(128);
        request.Metadata.Should().ContainKey("trace").WhoseValue.Should().Be("abc");
        request.Messages.Should().HaveCount(2);
        request.Messages[0].Role.Should().Be("system");
        request.Messages[1].ContentParts.Should().ContainSingle(x => x.Text == "part-text");
        request.RoutingContext!.MaxToolRoundsOverride.Should().Be(4);
        request.LlmControl!.UserMemoryPrompt.Should().Be("remember");

        events.Select(x => x.Payload.GetType()).Should().ContainInOrder(
            typeof(WorkflowLlmReasoningDeltaEvent),
            typeof(WorkflowLlmTextDeltaEvent),
            typeof(WorkflowLlmToolCallDeltaEvent),
            typeof(WorkflowLlmTextDeltaEvent),
            typeof(WorkflowLlmInvocationCompletedEvent));

        events.Select(x => x.Payload).OfType<WorkflowLlmTextDeltaEvent>()
            .Select(x => x.Delta)
            .Should().ContainInOrder("hel", "lo");
        events.Select(x => x.Payload).OfType<WorkflowLlmReasoningDeltaEvent>()
            .Single().Delta.Should().Be("thinking");
        events.Select(x => x.Payload).OfType<WorkflowLlmToolCallDeltaEvent>()
            .Single().ToolName.Should().Be("lookup");

        var completed = events.Select(x => x.Payload).OfType<WorkflowLlmInvocationCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Content.Should().Be("hello");
        completed.ReasoningContent.Should().Be("thinking");
        completed.ToolCalls.Should().ContainSingle().Which.CallId.Should().Be("call-1");
        completed.ContentEmitted.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldYieldFailureCompletion_WhenProviderFails()
    {
        var port = new WorkflowAiLlmInvocationPort(
            new RecordingProviderFactory(new RecordingProvider([], new InvalidOperationException("provider down"))),
            NullLogger<WorkflowAiLlmInvocationPort>.Instance);

        var events = await CollectAsync(port.InvokeAsync(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            TargetRole = "writer",
        }));

        var completed = events.Should().ContainSingle().Subject.Payload
            .Should().BeOfType<WorkflowLlmInvocationCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("provider down");
        completed.WorkerId.Should().Be("writer");
    }

    [Fact]
    public async Task InvokeAsync_ShouldPropagateCancellation()
    {
        var port = new WorkflowAiLlmInvocationPort(
            new RecordingProviderFactory(new CancelingProvider()),
            NullLogger<WorkflowAiLlmInvocationPort>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CollectAsync(port.InvokeAsync(new WorkflowLlmExecutionIntent(), cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<List<WorkflowLlmInvocationEvent>> CollectAsync(
        IAsyncEnumerable<WorkflowLlmInvocationEvent> events)
    {
        var list = new List<WorkflowLlmInvocationEvent>();
        await foreach (var evt in events)
            list.Add(evt);
        return list;
    }

    private sealed class RecordingProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name)
        {
            name.Should().Be("test-provider");
            return provider;
        }

        public ILLMProvider GetDefault() => provider;

        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class RecordingProvider(
        IReadOnlyList<LLMStreamChunk> chunks,
        Exception? failure = null) : ILLMProvider
    {
        public string Name => "test-provider";

        public List<LLMRequest> Calls { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add(request);
            if (failure != null)
                throw failure;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class CancelingProvider : ILLMProvider
    {
        public string Name => "canceling";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "unreachable" };
        }
    }
}
