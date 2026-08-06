using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunOrchestrationComponentTests
{
    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldFail_WhenProjectionIsDisabled()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("actor-1", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunCommandTargetResolver(
            actorResolver,
            new FakeProjectionPort { ProjectionEnabled = false },
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("auto"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldReturnTarget_WhenActorResolutionSucceeds()
    {
        var actor = new FakeActor("actor-1");
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt(actor.Id, string.Empty, ["definition-1", "actor-1"]), "auto", WorkflowChatRunStartError.None)),
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("auto"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("actor-1");
        result.Target.WorkflowName.Should().Be("auto");
        result.Target.CreatedActorIds.Should().Equal("definition-1", "actor-1");
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldUseTargetSeed_WithoutResolvingActorAgain()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunCommandTargetResolver(
            actorResolver,
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var request = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.CatalogWorkflow("direct"),
            ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var result = await resolver.ResolveAsync(request);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("run-1");
        result.Target.WorkflowName.Should().Be("direct");
        result.Target.CreatedActorIds.Should().Equal("definition-1", "run-1");
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldKeepProjectionDisabledCheck_WhenTargetSeedIsPresent()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunCommandTargetResolver(
            actorResolver,
            new FakeProjectionPort { ProjectionEnabled = false },
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var request = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.CatalogWorkflow("direct"),
            ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var result = await resolver.ResolveAsync(request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldRejectInvalidCallerCredential_BeforeTargetResolution()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunCommandTargetResolver(
            actorResolver,
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var request = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.CatalogWorkflow("direct"),
            ExternalCapabilityExecutionMode.Interactive,
            CallerCredential: new WorkflowCallerCredential("Bearer token-123"),
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var result = await resolver.ResolveAsync(request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldRejectSeed_WhenWorkflowNameDiffersFromRequest()
    {
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None)),
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var request = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.CatalogWorkflow("auto"),
            ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var result = await resolver.ResolveAsync(request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNameMismatch);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldRejectSeed_WhenActorBindingDiffersFromRequest()
    {
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None)),
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort(),
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var request = new WorkflowChatRunRequest(
            "hello",
            WorkflowChatSource.DefinitionActor("actor-2", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.DefinitionActor("actor-1", "direct")));

        var result = await resolver.ResolveAsync(request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTargetResolver_ShouldReturnAcceptedTarget_WithoutLiveObservationDependencies()
    {
        var actor = new FakeActor("actor-accepted");
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var resolver = new WorkflowRunAcceptedCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt(actor.Id, string.Empty, ["definition-1", "actor-accepted"]), "direct", WorkflowChatRunStartError.None)),
            actorPort);

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("actor-accepted");
        result.Target.WorkflowName.Should().Be("direct");
        result.Target.CreatedActorIds.Should().Equal("definition-1", "actor-accepted");
        projectionPort.AttachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTargetResolver_ShouldNotConsultProjectionReadiness()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("actor-1", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunAcceptedCommandTargetResolver(
            actorResolver,
            new FakeWorkflowRunActorPort());

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("auto"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeTrue();
        actorResolver.ResolveCallCount.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTargetResolver_ShouldPropagateActorResolutionError()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(null, string.Empty, WorkflowChatRunStartError.WorkflowNotFound));
        var resolver = new WorkflowRunAcceptedCommandTargetResolver(
            actorResolver,
            new FakeWorkflowRunActorPort());

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("missing"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
        result.Target.Should().BeNull();
        actorResolver.ResolveCallCount.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTargetResolver_ShouldRejectInvalidCallerCredential_BeforeActorResolution()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("unexpected", string.Empty, []), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunAcceptedCommandTargetResolver(
            actorResolver,
            new FakeWorkflowRunActorPort());

        var result = await resolver.ResolveAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.CatalogWorkflow("auto"),
                ExternalCapabilityExecutionMode.Interactive,
                CallerCredential: new WorkflowCallerCredential("Bearer token-123")));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
        result.Target.Should().BeNull();
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public void WorkflowRunAcceptedCommandTarget_ShouldExposeOnlyDispatchCleanupInterface()
    {
        var target = new WorkflowRunAcceptedCommandTarget("actor-accepted",
            "direct",
            [],
            new FakeWorkflowRunActorPort());

        target.Should().BeAssignableTo<ICommandDispatchTarget>();
        target.Should().BeAssignableTo<ICommandDispatchCleanupAware>();
        target.Should().NotBeAssignableTo<ICommandEventTarget<WorkflowRunEventEnvelope>>();
        target.Should().NotBeAssignableTo<ICommandDetachedContinuationTarget<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>>();
        target.Should().NotBeAssignableTo<ICommandInteractionCleanupTarget<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>>();
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTarget_ShouldDestroyOnlyActorsCreatedDuringResolution_OnDispatchFailureCleanup()
    {
        var actorPort = new FakeWorkflowRunActorPort();
        var target = new WorkflowRunAcceptedCommandTarget("actor-1",
            "direct",
            ["definition-1", "actor-1", "definition-1"],
            actorPort);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);
        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTarget_ShouldWrapSingleCleanupFailure()
    {
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("destroy failed"),
        };
        var target = new WorkflowRunAcceptedCommandTarget("actor-1",
            "direct",
            ["actor-1"],
            actorPort);

        var act = () => target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("Failed to destroy workflow actor 'actor-1'.");
        exception.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("destroy failed");
        actorPort.DestroyCalls.Should().Equal("actor-1");
    }

    [Fact]
    public async Task WorkflowRunAcceptedCommandTarget_ShouldAggregateCleanupFailures()
    {
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("destroy failed"),
        };
        var target = new WorkflowRunAcceptedCommandTarget("actor-1",
            "direct",
            ["definition-1", "actor-1"],
            actorPort);

        var act = () => target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.WithMessage("Workflow actor cleanup failed.*");
        exception.Which.InnerExceptions.Should().HaveCount(2);
        exception.Which.InnerExceptions.Should().AllSatisfy(ex =>
        {
            ex.Should().BeOfType<InvalidOperationException>();
            ex.InnerException.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("destroy failed");
        });
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public void WorkflowRunCommandTargetResolver_ShouldRejectMissingDurableCompletionResolver()
    {
        var projectionPort = new FakeProjectionPort();
        var act = () => new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt("actor-1", string.Empty, []), "direct", WorkflowChatRunStartError.None)),
            projectionPort,
            new FakeWorkflowRunActorPort(),
            durableCompletionResolver: null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("durableCompletionResolver");
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldWireDurableCompletionResolverIntoResolvedTarget()
    {
        var actor = new FakeActor("actor-1");
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var queryPort = new CompletingCurrentStateQueryPort();
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(new WorkflowRunCreationReceipt(actor.Id, string.Empty, ["definition-1", "actor-1"]), "direct", WorkflowChatRunStartError.None)),
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(queryPort));

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive), CancellationToken.None);
        result.Target.Should().NotBeNull();
        result.Target!.BindLiveObservation(
            new FakeProjectionLease("actor-1", "cmd-1"),
            null,
            new EventChannel<WorkflowRunEventEnvelope>());

        await result.Target.PublishDetachedCommandSignalAsync(
            new DetachedCommandTimeout<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1"),
                WorkflowProjectionCompletionStatus.Unknown),
            CancellationToken.None);
        // 06-20-observatory-run-state-feed (R2): created-actor reclaim is scheduled detached; await it so the
        // destroy is observed deterministically. (Resolver wired here without a gate → direct-destroy fallback.)
        await result.Target.PendingReclaimTask;

        queryPort.ActorIds.Should().Equal("actor-1");
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task WorkflowRunObservationLifecycle_ShouldAttachLeaseAndSink_OnSuccess()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var lifecycle = new WorkflowRunObservationLifecycle(projectionPort);
        var target = new WorkflowRunCommandTarget("actor-1",
            "direct",
            [],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        target.ProjectionLease.Should().BeSameAs(projectionPort.ExistingLease);
        target.LiveSink.Should().NotBeNull();
        projectionPort.AttachCalls.Should().ContainSingle()
            .Which.Lease.Should().BeSameAs(projectionPort.ExistingLease);
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "cmd-1"));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunObservationLifecycle_ShouldLeaveCreatedActorRollbackToInteractionOwner_WhenExistingProjectionIsUnavailable()
    {
        var projectionPort = new FakeProjectionPort
        {
            AttachExistingReturnsNull = true,
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var lifecycle = new WorkflowRunObservationLifecycle(projectionPort);
        var target = new WorkflowRunCommandTarget("actor-1",
            "direct",
            ["definition-1", "actor-1", "definition-1"],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        projectionPort.AttachCalls.Should().BeEmpty();
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "cmd-1"));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunObservationLifecycle_ShouldLeaveCreatedActorRollbackToInteractionOwner_WhenAttachFails()
    {
        var projectionPort = new FakeProjectionPort
        {
            AttachException = new InvalidOperationException("attach failed"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var lifecycle = new WorkflowRunObservationLifecycle(projectionPort);
        var target = new WorkflowRunCommandTarget("actor-1",
            "direct",
            ["definition-1", "actor-1"],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()));
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var act = () => lifecycle.BindAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            CreateExecution(target, context),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "cmd-1"));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    private static CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt> CreateExecution(
        WorkflowRunCommandTarget target,
        CommandContext context) =>
        new()
        {
            Target = target,
            Context = context,
            Envelope = new EventEnvelope { Id = "evt-1" },
            Receipt = new WorkflowChatRunAcceptedReceipt(
                target.ActorId,
                target.WorkflowName,
                context.CommandId,
                context.CorrelationId),
        };

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        private readonly WorkflowActorResolutionResult _result;
        public int ResolveCallCount { get; private set; }

        public FakeWorkflowRunActorResolver(WorkflowActorResolutionResult result)
        {
            _result = result;
        }

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            ResolveCallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled { get; set; } = true;
        public bool AttachExistingReturnsNull { get; set; }
        public Exception? AttachException { get; set; }
        public FakeProjectionLease ExistingLease { get; set; } = new("actor-1", "cmd-1");
        public List<(string RootActorId, string CommandId)> AttachExistingCalls { get; } = [];
        public List<(IWorkflowExecutionProjectionLease Lease, IEventSink<WorkflowRunEventEnvelope> Sink)> AttachCalls { get; } = [];

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (AttachException != null)
                throw AttachException;

            AttachCalls.Add((lease, sink));
            return Task.FromResult<IAsyncDisposable?>(new FakeLiveSinkLease());
        }

        public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachExistingCalls.Add((rootActorId, commandId));
            if (AttachExistingReturnsNull)
                return null;

            var liveSinkLease = await AttachLiveSinkAsync(ExistingLease, sink, ct);
            return liveSinkLease == null
                ? null
                : new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(ExistingLease, liveSinkLease);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeLiveSinkLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public List<string> DestroyCalls { get; } = [];
        public Exception? DestroyException { get; set; }
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyCalls.Add(actorId);
            if (DestroyException != null)
                throw DestroyException;

            return Task.CompletedTask;
        }

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

    private sealed class FakeProjectionLease : IWorkflowExecutionProjectionLease
    {
        public FakeProjectionLease(string actorId, string commandId)
        {
            ActorId = actorId;
            CommandId = commandId;
        }

        public string ActorId { get; }
        public string CommandId { get; }
    }

    private sealed class CompletingCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public List<string> ActorIds { get; } = [];

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ActorIds.Add(actorId);
            return Task.FromResult<WorkflowActorSnapshot?>(
                new WorkflowActorSnapshot { CompletionStatus = WorkflowRunCompletionStatus.Completed });
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(int take = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(string actorId, CancellationToken ct = default) =>
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
