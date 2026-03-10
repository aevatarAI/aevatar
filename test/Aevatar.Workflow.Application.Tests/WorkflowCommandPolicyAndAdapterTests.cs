using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowCommandPolicyAndAdapterTests
{
    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldValidateTarget()
    {
        var policy = new WorkflowCommandContextPolicy();

        Action act = () => policy.Create(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldGenerateIdsAndCopyMetadata()
    {
        var policy = new WorkflowCommandContextPolicy();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["k1"] = "v1",
        };

        var context = policy.Create("actor-1", metadata);

        context.TargetId.Should().Be("actor-1");
        context.CommandId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().Be(context.CommandId);
        context.Metadata.Should().ContainKey("k1").WhoseValue.Should().Be("v1");

        metadata["k1"] = "mutated";
        context.Metadata["k1"].Should().Be("v1");
    }

    [Fact]
    public void WorkflowCommandContextPolicy_Create_ShouldRespectProvidedIds()
    {
        var policy = new WorkflowCommandContextPolicy();

        var context = policy.Create(
            "actor-2",
            commandId: "cmd-2",
            correlationId: "corr-2");

        context.CommandId.Should().Be("cmd-2");
        context.CorrelationId.Should().Be("corr-2");
    }

    [Fact]
    public void WorkflowRunAcceptedReceiptFactory_ShouldCreateReceiptFromTargetAndContext()
    {
        var projectionPort = new NoOpProjectionPort();
        var actor = new FakeActor("actor-1");
        var target = new WorkflowRunCommandTarget(
            actor,
            "direct",
            createdActorIds: [],
            projectionPort,
            new NoOpWorkflowRunActorPort());
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());
        var factory = new WorkflowRunAcceptedReceiptFactory();

        var receipt = factory.Create(target, context);

        receipt.Should().Be(new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1"));
    }

    private sealed class NoOpProjectionPort : IWorkflowExecutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled => true;

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<IWorkflowExecutionProjectionLease?>(null);

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent(id + "-agent");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
