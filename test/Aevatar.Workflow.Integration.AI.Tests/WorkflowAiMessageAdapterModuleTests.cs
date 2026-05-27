using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Integration.AI.Tests;

public sealed class WorkflowAiMessageAdapterModuleTests
{
    [Fact]
    public async Task HandleAsync_ShouldPublishStartedStreamEventsAndCompletion_ForWorkflowIntent()
    {
        var module = new WorkflowAiMessageAdapterModule(new StubInvocationPort(
        [
            new WorkflowLlmInvocationEvent(new WorkflowLlmTextDeltaEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                SessionId = "session-1",
                Delta = "hello",
            }),
            new WorkflowLlmInvocationEvent(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                SessionId = "session-1",
                Success = true,
                Content = "hello",
            }),
        ]));
        var ctx = new TestWorkflowExecutionContext();

        await module.HandleAsync(Envelope(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            TargetRole = "writer",
        }), ctx, CancellationToken.None);

        ctx.Published.Select(x => x.Event.GetType()).Should().ContainInOrder(
            typeof(WorkflowLlmInvocationStartedEvent),
            typeof(WorkflowLlmTextDeltaEvent),
            typeof(WorkflowLlmInvocationCompletedEvent));
        ctx.Published.Should().OnlyContain(x => x.Audience == TopologyAudience.Self);
    }

    [Fact]
    public async Task HandleAsync_ShouldMapWorkflowChatRequestToAiChatRequest_WhenIntentIsAbsent()
    {
        var module = new WorkflowAiMessageAdapterModule(new StubInvocationPort([]));
        var ctx = new TestWorkflowExecutionContext();

        await module.HandleAsync(Envelope(new WorkflowChatRequestEvent
        {
            Prompt = "prompt",
            SessionId = "session-1",
            ScopeId = "scope-1",
            Headers = { ["h"] = "v" },
            Metadata = { ["m"] = "n" },
            InputParts =
            {
                new WorkflowChatContentPart
                {
                    Kind = WorkflowChatContentPartKind.Image,
                    DataBase64 = "abc",
                    MediaType = "image/png",
                    Name = "image",
                },
            },
        }), ctx, CancellationToken.None);

        var chat = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<ChatRequestEvent>().Subject;
        chat.Prompt.Should().Be("prompt");
        chat.SessionId.Should().Be("session-1");
        chat.ScopeId.Should().Be("scope-1");
        chat.Headers.Should().ContainKey("h").WhoseValue.Should().Be("v");
        chat.Metadata.Should().ContainKey("m").WhoseValue.Should().Be("n");
        chat.InputParts.Should().ContainSingle().Which.Kind.Should().Be(ChatContentPartKind.Image);
    }

    [Fact]
    public async Task HandleAsync_ShouldMapAiResponseAndEndEventsToWorkflowEvents()
    {
        var module = new WorkflowAiMessageAdapterModule(new StubInvocationPort([]));
        var ctx = new TestWorkflowExecutionContext();

        await module.HandleAsync(Envelope(new ChatResponseEvent
        {
            Content = "reply",
            SessionId = "session-1",
        }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new TextMessageEndEvent
        {
            Content = "final",
            SessionId = "session-2",
        }), ctx, CancellationToken.None);

        ctx.Published[0].Event.Should().BeOfType<WorkflowChatResponseEvent>()
            .Which.Content.Should().Be("reply");
        ctx.Published[1].Event.Should().BeOfType<WorkflowTextMessageEndEvent>()
            .Which.Content.Should().Be("final");
    }

    [Fact]
    public async Task HandleAsync_ShouldIgnoreUnsupportedOrEmptyPayload()
    {
        var module = new WorkflowAiMessageAdapterModule(new StubInvocationPort([]));
        var ctx = new TestWorkflowExecutionContext();

        await module.HandleAsync(new EventEnvelope(), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StringValue { Value = "unsupported" }), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
        };

    private sealed class StubInvocationPort(IReadOnlyList<WorkflowLlmInvocationEvent> events) : IWorkflowLlmInvocationPort
    {
        public async IAsyncEnumerable<WorkflowLlmInvocationEvent> InvokeAsync(
            WorkflowLlmExecutionIntent intent,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = intent;
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
