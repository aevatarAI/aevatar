using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunControlCommandTests
{
    [Fact]
    public async Task ResumeResolver_ShouldResolveRunActor_WhenBindingMatches()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var resolver = new WorkflowResumeCommandTargetResolver(
            runtime,
            new FakeWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "actor-1",
                    "definition-1",
                    "run-1",
                    "direct",
                    "yaml",
                    new Dictionary<string, string>())));

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand("actor-1", "run-1", "step-1", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("actor-1");
        result.Target.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task SignalResolver_ShouldReturnMismatchError_WhenBindingDiffers()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var resolver = new WorkflowSignalCommandTargetResolver(
            runtime,
            new FakeWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "actor-1",
                    "definition-1",
                    "run-expected",
                    "direct",
                    "yaml",
                    new Dictionary<string, string>())));

        var result = await resolver.ResolveAsync(
            new WorkflowSignalCommand("actor-1", "run-other", "approve", "cmd-1", "yes"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected"));
    }

    [Fact]
    public async Task ResumeResolver_ShouldRejectBlankStepId_BeforeRuntimeLookup()
    {
        var runtime = new FakeActorRuntime();
        var resolver = new WorkflowResumeCommandTargetResolver(
            runtime,
            new FakeWorkflowActorBindingReader(null));

        var result = await resolver.ResolveAsync(
            new WorkflowResumeCommand("actor-1", "run-1", " ", "cmd-1", true, "approved"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " "));
    }

    [Fact]
    public async Task SignalResolver_ShouldRejectBlankSignalName_BeforeRuntimeLookup()
    {
        var runtime = new FakeActorRuntime();
        var resolver = new WorkflowSignalCommandTargetResolver(
            runtime,
            new FakeWorkflowActorBindingReader(null));

        var result = await resolver.ResolveAsync(
            new WorkflowSignalCommand("actor-1", "run-1", " ", "cmd-1", "yes"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowRunControlStartError.InvalidSignalName("actor-1", "run-1", " "));
    }

    [Fact]
    public async Task StopResolver_ShouldResolveRunActor_WhenBindingMatches()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var resolver = new WorkflowStopCommandTargetResolver(
            runtime,
            new FakeWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "actor-1",
                    "definition-1",
                    "run-1",
                    "direct",
                    "yaml",
                    new Dictionary<string, string>())));

        var result = await resolver.ResolveAsync(
            new WorkflowStopCommand("actor-1", "run-1", "cmd-1", "user requested stop"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("actor-1");
        result.Target.RunId.Should().Be("run-1");
    }

    [Fact]
    public void ResumeEnvelopeFactory_ShouldPackWorkflowResumedEvent()
    {
        var factory = new WorkflowResumeCommandEnvelopeFactory();
        var envelope = factory.CreateEnvelope(
            new WorkflowResumeCommand(
                "actor-1",
                "run-1",
                "step-1",
                "cmd-1",
                true,
                "approved",
                new Dictionary<string, string>
                {
                    ["source"] = "tests",
                },
                "edited draft",
                "minor note"),
            new CommandContext("actor-1", "cmd-1", "cmd-1", new Dictionary<string, string>()));

        var resumed = envelope.Payload.Unpack<WorkflowResumedEvent>();

        envelope.Route!.PublisherActorId.Should().Be("api.workflow.resume");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("cmd-1");
        resumed.RunId.Should().Be("run-1");
        resumed.StepId.Should().Be("step-1");
        resumed.Approved.Should().BeTrue();
        resumed.UserInput.Should().Be("approved");
        resumed.EditedContent.Should().Be("edited draft");
        resumed.Feedback.Should().Be("minor note");
        resumed.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("tests");
    }

    [Fact]
    public void ResumeEnvelopeFactory_ShouldRejectBlankStepId()
    {
        var factory = new WorkflowResumeCommandEnvelopeFactory();

        var act = () => factory.CreateEnvelope(
            new WorkflowResumeCommand("actor-1", "run-1", " ", "cmd-1", true, "approved"),
            new CommandContext("actor-1", "cmd-1", "cmd-1", new Dictionary<string, string>()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SignalEnvelopeFactory_ShouldPackSignalReceivedEvent()
    {
        var factory = new WorkflowSignalCommandEnvelopeFactory();
        var envelope = factory.CreateEnvelope(
            new WorkflowSignalCommand("actor-1", "run-1", "approve", "cmd-1", "yes"),
            new CommandContext("actor-1", "cmd-1", "cmd-1", new Dictionary<string, string>()));

        var signal = envelope.Payload.Unpack<SignalReceivedEvent>();

        envelope.Route!.PublisherActorId.Should().Be("api.workflow.signal");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("cmd-1");
        signal.RunId.Should().Be("run-1");
        signal.SignalName.Should().Be("approve");
        signal.Payload.Should().Be("yes");
    }

    [Fact]
    public void SignalEnvelopeFactory_ShouldRejectBlankSignalName()
    {
        var factory = new WorkflowSignalCommandEnvelopeFactory();

        var act = () => factory.CreateEnvelope(
            new WorkflowSignalCommand("actor-1", "run-1", " ", "cmd-1", "yes"),
            new CommandContext("actor-1", "cmd-1", "cmd-1", new Dictionary<string, string>()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StopEnvelopeFactory_ShouldPackWorkflowStoppedEvent()
    {
        var factory = new WorkflowStopCommandEnvelopeFactory();
        var envelope = factory.CreateEnvelope(
            new WorkflowStopCommand("actor-1", "run-1", "cmd-1", "user requested stop"),
            new CommandContext("actor-1", "cmd-1", "cmd-1", new Dictionary<string, string>()));

        var stopped = envelope.Payload.Unpack<WorkflowStoppedEvent>();

        envelope.Route!.PublisherActorId.Should().Be("api.workflow.stop");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("cmd-1");
        stopped.RunId.Should().Be("run-1");
        stopped.Reason.Should().Be("user requested stop");
    }

    [Fact]
    public void AcceptedReceiptFactory_ShouldUseContextIdentity()
    {
        var factory = new WorkflowRunControlAcceptedReceiptFactory();
        var receipt = factory.Create(
            new WorkflowRunControlCommandTarget(new FakeActor("actor-1"), "run-1"),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        receipt.ActorId.Should().Be("actor-1");
        receipt.RunId.Should().Be("run-1");
        receipt.CommandId.Should().Be("cmd-1");
        receipt.CorrelationId.Should().Be("corr-1");
    }

    private sealed class FakeWorkflowActorBindingReader(WorkflowActorBinding? binding) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(binding);
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new FakeAgent(id + "-agent");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
