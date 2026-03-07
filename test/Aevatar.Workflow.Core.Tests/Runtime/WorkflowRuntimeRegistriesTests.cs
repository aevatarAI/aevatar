using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Runtime;

public class WorkflowRuntimeRegistriesTests
{
    [Fact]
    public async Task TryHandleStatefulCompletionAsync_ShouldStopAfterFirstSuccessfulHandler()
    {
        var first = new TestCompletionHandler(false);
        var second = new TestCompletionHandler(true);
        var third = new TestCompletionHandler(true);
        var reconciler = new WorkflowAsyncOperationReconciler(
            new WorkflowStatefulCompletionHandlerRegistry([first, second, third]),
            new WorkflowInternalSignalRegistry([]),
            new WorkflowResponseHandlerRegistry([]));

        var handled = await reconciler.TryHandleStatefulCompletionAsync(new StepCompletedEvent(), CancellationToken.None);

        handled.Should().BeTrue();
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        third.Calls.Should().Be(0);
    }

    [Fact]
    public async Task HandleRuntimeCallbackEnvelopeAsync_ShouldDispatchToFirstMatchingInternalSignalHandler()
    {
        var first = new TestInternalSignalHandler(canHandle: false);
        var second = new TestInternalSignalHandler(canHandle: true);
        var third = new TestInternalSignalHandler(canHandle: true);
        var reconciler = new WorkflowAsyncOperationReconciler(
            new WorkflowStatefulCompletionHandlerRegistry([]),
            new WorkflowInternalSignalRegistry([first, second, third]),
            new WorkflowResponseHandlerRegistry([]));

        await reconciler.HandleRuntimeCallbackEnvelopeAsync(
            CreateEnvelope(Any.Pack(new DelayStepTimeoutFiredEvent { RunId = "run-1", StepId = "step-1" })),
            CancellationToken.None);

        first.HandledEnvelopes.Should().BeEmpty();
        second.HandledEnvelopes.Should().ContainSingle();
        third.HandledEnvelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRoleAndPromptResponseEnvelopeAsync_ShouldDispatchToFirstSuccessfulResponseHandler()
    {
        var first = new TestResponseHandler(false);
        var second = new TestResponseHandler(true);
        var third = new TestResponseHandler(true);
        var reconciler = new WorkflowAsyncOperationReconciler(
            new WorkflowStatefulCompletionHandlerRegistry([]),
            new WorkflowInternalSignalRegistry([]),
            new WorkflowResponseHandlerRegistry([first, second, third]));

        await reconciler.HandleRoleAndPromptResponseEnvelopeAsync(
            CreateEnvelope(Any.Pack(new ChatResponseEvent
            {
                SessionId = "session-1",
                Content = "ok",
            }), publisherId: "role-1"),
            defaultPublisherId: "default-role",
            CancellationToken.None);

        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        third.Calls.Should().Be(0);
        second.LastDefaultPublisherId.Should().Be("default-role");
    }

    [Fact]
    public async Task ChildRunCompletionRegistry_ShouldStopAfterFirstSuccessfulHandler()
    {
        var first = new TestChildRunCompletionHandler(false);
        var second = new TestChildRunCompletionHandler(true);
        var third = new TestChildRunCompletionHandler(true);
        var registry = new WorkflowChildRunCompletionRegistry([first, second, third]);

        var handled = await registry.TryHandleAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run",
                WorkflowName = "child",
                Success = true,
            },
            publisherActorId: "child-actor",
            CancellationToken.None);

        handled.Should().BeTrue();
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        third.Calls.Should().Be(0);
    }

    private static EventEnvelope CreateEnvelope(Any payload, string publisherId = "publisher") =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = payload,
            PublisherId = publisherId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed class TestCompletionHandler(bool shouldHandle) : IWorkflowStatefulCompletionHandler
    {
        public int Calls { get; private set; }

        public Task<bool> TryHandleCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(shouldHandle);
        }
    }

    private sealed class TestInternalSignalHandler(bool canHandle) : IWorkflowInternalSignalHandler
    {
        public List<EventEnvelope> HandledEnvelopes { get; } = [];

        public bool CanHandle(EventEnvelope envelope) => canHandle;

        public Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
        {
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class TestResponseHandler(bool shouldHandle) : IWorkflowResponseHandler
    {
        public int Calls { get; private set; }

        public string? LastDefaultPublisherId { get; private set; }

        public Task<bool> TryHandleAsync(EventEnvelope envelope, string defaultPublisherId, CancellationToken ct)
        {
            Calls++;
            LastDefaultPublisherId = defaultPublisherId;
            return Task.FromResult(shouldHandle);
        }
    }

    private sealed class TestChildRunCompletionHandler(bool shouldHandle) : IWorkflowChildRunCompletionHandler
    {
        public int Calls { get; private set; }

        public Task<bool> TryHandleAsync(WorkflowCompletedEvent evt, string? publisherActorId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(shouldHandle);
        }
    }
}
