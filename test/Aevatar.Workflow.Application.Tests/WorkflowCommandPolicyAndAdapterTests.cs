using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.CQRS.Core.Commands;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowCommandPolicyAndAdapterTests
{
    [Fact]
    public void DefaultCommandContextPolicy_Create_ShouldValidateTarget()
    {
        var policy = new DefaultCommandContextPolicy();

        Action act = () => policy.Create(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DefaultCommandContextPolicy_Create_ShouldGenerateIdsAndCopyHeaders()
    {
        var policy = new DefaultCommandContextPolicy();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["k1"] = "v1",
        };

        var context = policy.Create("actor-1", headers);

        context.TargetId.Should().Be("actor-1");
        context.CommandId.Should().NotBeNullOrWhiteSpace();
        context.CorrelationId.Should().Be(context.CommandId);
        context.Headers.Should().ContainKey("k1").WhoseValue.Should().Be("v1");

        headers["k1"] = "mutated";
        context.Headers["k1"].Should().Be("v1");
    }

    [Fact]
    public void DefaultCommandContextPolicy_Create_ShouldRespectProvidedIds()
    {
        var policy = new DefaultCommandContextPolicy();

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
        var target = new WorkflowRunCommandTarget(
            "actor-1",
            "direct",
            createdActorIds: [],
            projectionPort,
            new NoOpWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());
        var factory = new WorkflowRunAcceptedReceiptFactory();

        var receipt = factory.Create(target, context);

        receipt.Should().Be(new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1"));
    }

    [Fact]
    public void WorkflowRunAcceptedReceiptFactory_ShouldCreateReceiptFromAcceptedTargetAndContext()
    {
        var target = new WorkflowRunAcceptedCommandTarget("actor-accepted",
            "direct",
            createdActorIds: [],
            new NoOpWorkflowRunActorPort());
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-accepted",
            "cmd-accepted",
            "corr-accepted",
            new Dictionary<string, string>());
        var factory = new WorkflowRunAcceptedReceiptFactory();
        var typedFactory = (Aevatar.CQRS.Core.Abstractions.Commands.ICommandReceiptFactory<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>)factory;

        var receipt = typedFactory.Create(target, context);

        receipt.Should().Be(new WorkflowChatRunAcceptedReceipt("actor-accepted", "direct", "cmd-accepted", "corr-accepted"));
    }

    private sealed class NoOpProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(null);

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?>(null);

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
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
