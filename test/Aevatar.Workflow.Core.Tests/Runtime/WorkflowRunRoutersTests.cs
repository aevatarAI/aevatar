using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Runtime;

public class WorkflowRunRoutersTests
{
    [Fact]
    public async Task StepRouter_ShouldDispatchToMatchingCapability()
    {
        var harness = CreateHarness(new WorkflowRunState
        {
            RunId = "run-1",
        });
        var capability = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "control",
            SupportedStepTypes: ["delay"]));
        capability.OnStepAsync = (request, _, _, _, _) =>
        {
            capability.HandledSteps.Add(request);
            return Task.CompletedTask;
        };
        var router = new WorkflowRunStepRouter(
            new WorkflowRunCapabilityRegistry([capability]),
            new WorkflowPrimitiveExecutorRegistry([]),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "delay" },
            () => null,
            () => new NullLogger(),
            harness.Read,
            harness.Write,
            harness.Effects);

        var request = new StepRequestEvent
        {
            StepId = "delay-1",
            StepType = "DELAY",
            RunId = "run-1",
        };

        await router.DispatchAsync(request, CancellationToken.None);

        capability.HandledSteps.Should().ContainSingle().Which.Should().BeSameAs(request);
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ResponseRouter_ShouldRejectAmbiguousCapabilityMatch()
    {
        var harness = CreateHarness(new WorkflowRunState
        {
            RunId = "run-1",
        });
        var route = WorkflowCapabilityRoutes.For<ChatResponseEvent>();
        var first = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "first",
            SupportedResponseTypeUrls: [route]));
        first.CanHandleResponseFunc = (_, _, _) => true;
        var second = new TestCapability(new WorkflowRunCapabilityDescriptor(
            Name: "second",
            SupportedResponseTypeUrls: [route]));
        second.CanHandleResponseFunc = (_, _, _) => true;
        var router = new WorkflowRunResponseRouter(
            new WorkflowRunCapabilityRegistry([first, second]),
            harness.Read,
            harness.Write,
            harness.Effects);

        var envelope = CreateEnvelope(Any.Pack(new ChatResponseEvent
        {
            SessionId = "session-1",
            Content = "ok",
        }));

        var act = () => router.TryHandleAsync(envelope, "publisher-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Multiple workflow run capabilities matched response*");
    }

    [Fact]
    public async Task ResumeRouter_ShouldDispatchToSingleMatchingCapability()
    {
        var harness = CreateHarness(new WorkflowRunState
        {
            RunId = "run-1",
        });
        var capability = new TestCapability(new WorkflowRunCapabilityDescriptor(Name: "human"));
        capability.CanHandleResumeFunc = (evt, _) => evt.ResumeToken == "resume-1";
        capability.OnResumeAsync = (evt, _, _, _, _) =>
        {
            capability.HandledResumes.Add(evt);
            return Task.CompletedTask;
        };
        var router = new WorkflowRunResumeRouter(
            new WorkflowRunCapabilityRegistry([capability]),
            harness.Read,
            harness.Write,
            harness.Effects);

        await router.TryHandleAsync(new WorkflowResumedEvent
        {
            RunId = "run-1",
            ResumeToken = "resume-1",
            Approved = true,
        }, CancellationToken.None);

        capability.HandledResumes.Should().ContainSingle();
    }

    private static Harness CreateHarness(WorkflowRunState state)
    {
        var persisted = new List<WorkflowRunState>();
        var published = new List<IMessage>();
        var sent = new List<(string TargetActorId, IMessage Event)>();

        return new Harness(
            new WorkflowRunReadContext(
                actorIdAccessor: () => "workflow-run-1",
                stateAccessor: () => state,
                compiledWorkflowAccessor: () => null),
            new WorkflowRunWriteContext(
                persistStateAsync: (next, _) =>
                {
                    persisted.Add(next.Clone());
                    return Task.CompletedTask;
                },
                publishAsync: (evt, _, _) =>
                {
                    published.Add(evt);
                    return Task.CompletedTask;
                },
                sendToAsync: (targetActorId, evt, _) =>
                {
                    sent.Add((targetActorId, evt));
                    return Task.CompletedTask;
                },
                logWarningAsync: (_, _, _) => Task.CompletedTask),
            new WorkflowRunEffectPorts(
                ensureAgentTreeAsync: _ => Task.CompletedTask,
                scheduleWorkflowCallbackAsync: (_, _, _, _, _, _, _, _) => Task.CompletedTask,
                resolveOrCreateSubWorkflowRunActorAsync: (_, _) => Task.FromResult<IActor>(new NullActor("child")),
                linkChildAsync: (_, _) => Task.CompletedTask,
                cleanupChildWorkflowAsync: (_, _) => Task.CompletedTask,
                resolveWorkflowYamlAsync: (_, _) => Task.FromResult("name: child"),
                createWorkflowDefinitionBindEnvelope: (_, _) => new EventEnvelope(),
                createRoleAgentInitializeEnvelope: _ => new EventEnvelope(),
                dispatchWorkflowStepAsync: (_, _, _, _) => Task.CompletedTask,
                dispatchInternalStepAsync: (_, _, _, _, _, _, _, _) => Task.CompletedTask,
                dispatchWhileIterationAsync: (_, _, _) => Task.CompletedTask,
                finalizeRunAsync: (_, _, _, _) => Task.CompletedTask),
            persisted,
            published,
            sent);
    }

    private static EventEnvelope CreateEnvelope(Any payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = payload,
            PublisherId = "publisher-1",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed record Harness(
        WorkflowRunReadContext Read,
        WorkflowRunWriteContext Write,
        WorkflowRunEffectPorts Effects,
        List<WorkflowRunState> Persisted,
        List<IMessage> Published,
        List<(string TargetActorId, IMessage Event)> Sent);

    private sealed class TestCapability(IWorkflowRunCapabilityDescriptor descriptor) : IWorkflowRunCapability
    {
        public IWorkflowRunCapabilityDescriptor Descriptor { get; } = descriptor;

        public Func<StepRequestEvent, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnStepAsync { get; set; }

        public Func<StepCompletedEvent, WorkflowRunReadContext, bool>? CanHandleCompletionFunc { get; set; }

        public Func<StepCompletedEvent, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnCompletionAsync { get; set; }

        public Func<EventEnvelope, WorkflowRunReadContext, bool>? CanHandleInternalSignalFunc { get; set; }

        public Func<EventEnvelope, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnInternalSignalAsync { get; set; }

        public Func<EventEnvelope, string, WorkflowRunReadContext, bool>? CanHandleResponseFunc { get; set; }

        public Func<EventEnvelope, string, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnResponseAsync { get; set; }

        public Func<WorkflowCompletedEvent, string?, WorkflowRunReadContext, bool>? CanHandleChildRunCompletionFunc { get; set; }

        public Func<WorkflowCompletedEvent, string?, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnChildRunCompletionAsync { get; set; }

        public Func<WorkflowResumedEvent, WorkflowRunReadContext, bool>? CanHandleResumeFunc { get; set; }

        public Func<WorkflowResumedEvent, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnResumeAsync { get; set; }

        public Func<SignalReceivedEvent, WorkflowRunReadContext, bool>? CanHandleExternalSignalFunc { get; set; }

        public Func<SignalReceivedEvent, WorkflowRunReadContext, WorkflowRunWriteContext, WorkflowRunEffectPorts, CancellationToken, Task>? OnExternalSignalAsync { get; set; }

        public List<StepRequestEvent> HandledSteps { get; } = [];

        public List<WorkflowResumedEvent> HandledResumes { get; } = [];

        public Task HandleStepAsync(
            StepRequestEvent request,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnStepAsync?.Invoke(request, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) =>
            CanHandleCompletionFunc?.Invoke(evt, read) ?? false;

        public Task HandleCompletionAsync(
            StepCompletedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnCompletionAsync?.Invoke(evt, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read) =>
            CanHandleInternalSignalFunc?.Invoke(envelope, read) ?? false;

        public Task HandleInternalSignalAsync(
            EventEnvelope envelope,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnInternalSignalAsync?.Invoke(envelope, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleResponse(
            EventEnvelope envelope,
            string defaultPublisherId,
            WorkflowRunReadContext read) =>
            CanHandleResponseFunc?.Invoke(envelope, defaultPublisherId, read) ?? false;

        public Task HandleResponseAsync(
            EventEnvelope envelope,
            string defaultPublisherId,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnResponseAsync?.Invoke(envelope, defaultPublisherId, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleChildRunCompletion(
            WorkflowCompletedEvent evt,
            string? publisherActorId,
            WorkflowRunReadContext read) =>
            CanHandleChildRunCompletionFunc?.Invoke(evt, publisherActorId, read) ?? false;

        public Task HandleChildRunCompletionAsync(
            WorkflowCompletedEvent evt,
            string? publisherActorId,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnChildRunCompletionAsync?.Invoke(evt, publisherActorId, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) =>
            CanHandleResumeFunc?.Invoke(evt, read) ?? false;

        public Task HandleResumeAsync(
            WorkflowResumedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnResumeAsync?.Invoke(evt, read, write, effects, ct) ?? Task.CompletedTask;

        public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) =>
            CanHandleExternalSignalFunc?.Invoke(evt, read) ?? false;

        public Task HandleExternalSignalAsync(
            SignalReceivedEvent evt,
            WorkflowRunReadContext read,
            WorkflowRunWriteContext write,
            WorkflowRunEffectPorts effects,
            CancellationToken ct) =>
            OnExternalSignalAsync?.Invoke(evt, read, write, effects, ct) ?? Task.CompletedTask;
    }

    private sealed class NullActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
