using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Runtime;

public class WorkflowRunCapabilityRegistryTests
{
    [Fact]
    public void Constructor_ShouldRejectDuplicateCanonicalStepTypes()
    {
        var first = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "first",
            SupportedStepTypes: ["llm_call"]));
        var second = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "second",
            SupportedStepTypes: ["LLM_CALL"]));

        var act = () => new WorkflowRunCapabilityRegistry([first, second]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*llm_call*");
    }

    [Fact]
    public void TryGetStepCapability_ShouldUseCanonicalStepTypeLookup()
    {
        var capability = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "control",
            SupportedStepTypes: ["wait_signal"]));
        var registry = new WorkflowRunCapabilityRegistry([capability]);

        var handled = registry.TryGetStepCapability("WAIT_SIGNAL", out var resolved);

        handled.Should().BeTrue();
        resolved.Should().BeSameAs(capability);
    }

    [Fact]
    public void GetResponseCandidates_ShouldUseExactTypeUrlGrouping()
    {
        var url = Any.Pack(new ChatResponseEvent()).TypeUrl;
        var first = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "first",
            SupportedResponseTypeUrls: [url]));
        var second = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "second",
            SupportedResponseTypeUrls: [url]));
        var third = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "third",
            SupportedResponseTypeUrls: [Any.Pack(new TextMessageEndEvent()).TypeUrl]));

        var registry = new WorkflowRunCapabilityRegistry([first, second, third]);

        var candidates = registry.GetResponseCandidates(Any.Pack(new ChatResponseEvent
        {
            SessionId = "s",
            Content = "ok",
        }));

        candidates.Should().BeEquivalentTo([first, second], options => options.WithStrictOrdering());
    }

    private sealed class TestCapability(IWorkflowRunCapabilityDescriptor descriptor) : IWorkflowRunCapability
    {
        public IWorkflowRunCapabilityDescriptor Descriptor { get; } = descriptor;

        public Task HandleStepAsync(
            StepRequestEvent request,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) => false;

        public Task HandleCompletionAsync(
            StepCompletedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read) => false;

        public Task HandleInternalSignalAsync(
            EventEnvelope envelope,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleResponse(
            EventEnvelope envelope,
            string defaultPublisherId,
            WorkflowRunReadContext read) =>
            false;

        public Task HandleResponseAsync(
            EventEnvelope envelope,
            string defaultPublisherId,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleChildRunCompletion(
            WorkflowCompletedEvent evt,
            string? publisherActorId,
            WorkflowRunReadContext read) =>
            false;

        public Task HandleChildRunCompletionAsync(
            WorkflowCompletedEvent evt,
            string? publisherActorId,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

        public Task HandleResumeAsync(
            WorkflowResumedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;

        public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

        public Task HandleExternalSignalAsync(
            SignalReceivedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            Task.CompletedTask;
    }
}
