using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatRunInteractionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldReturnProjectionDisabled_BeforeActorResolutionOrActivation()
    {
        var actorResolver = new RecordingActorResolver();
        var activationPort = new RecordingActivationPort();
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort { ProjectionEnabled = false },
            activationPort,
            new RecordingRunProvisioningPort(),
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorResolver.Requests.Should().BeEmpty();
        activationPort.Activations.Should().BeEmpty();
        inner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveActivateAndInvokeInnerWithSeedsAndTargetSeed()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var inner = new RecordingInteractionService();
        var acceptedReceipts = new List<WorkflowChatRunAcceptedReceipt>();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace"] = "trace-1",
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            new RecordingRunProvisioningPort(),
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), Headers: headers),
            static (_, _) => ValueTask.CompletedTask,
            (receipt, _) =>
            {
                acceptedReceipts.Add(receipt);
                return ValueTask.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        actorResolver.Requests.Should().ContainSingle();
        activationPort.Activations.Should().ContainSingle()
            .Which.ActorId.Should().Be("run-1");
        inner.Requests.Should().ContainSingle();
        var command = inner.Requests.Single();
        command.TargetSeed.Should().NotBeNull();
        command.TargetSeed!.ActorId.Should().Be("run-1");
        command.TargetSeed.WorkflowNameForRun.Should().Be("direct");
        command.TargetSeed.CreatedActorIds.Should().Equal("definition-1", "run-1");
        command.CommandIdSeed.Should().Be(activationPort.Activations.Single().CommandId);
        command.CorrelationIdSeed.Should().NotBeNullOrWhiteSpace();
        command.Headers.Should().BeSameAs(headers);
        acceptedReceipts.Should().ContainSingle();
        acceptedReceipts[0].CommandId.Should().Be(command.CommandIdSeed);
        acceptedReceipts[0].CorrelationId.Should().Be(command.CorrelationIdSeed);
        activationPort.Releases.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnProjectionUnavailableAndRollback_WhenActivationFails()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort
        {
            ReturnNull = true,
        };
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        inner.Requests.Should().BeEmpty();
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        activationPort.Releases.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReleaseActivationAndRollback_WhenInnerFailsBeforeAccepted()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            Result = CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.ProjectionUnavailable),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations.Single());
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDestroyCreatedActorsOnceAndReleaseActivationOnce_WhenAttachMissOccursAfterNewRun()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var projectionPort = new RecordingProjectionPort
        {
            AttachExistingReturnsNull = true,
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            activationPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("run-1", activationPort.Activations.Single().CommandId));
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations.Single());
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDestroyCreatedActorsOnceAndReleaseActivationOnce_WhenAttachExistingThrowsAfterNewRun()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var projectionPort = new RecordingProjectionPort
        {
            AttachExistingException = new InvalidOperationException("attach failed"),
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            activationPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("run-1", activationPort.Activations.Single().CommandId));
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations.Single());
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDestroyCreatedActorsOnceAndReleaseActivationOnce_WhenDispatchThrowsAfterAttachForNewRun()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var projectionPort = new RecordingProjectionPort();
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            activationPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.Should().Be(("run-1", activationPort.Activations.Single().CommandId));
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations.Single());
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReleaseActivation_WhenInnerAccepts()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            AcceptBeforeReturningFailure = true,
            Result = CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.ProjectionUnavailable),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        activationPort.Releases.Should().BeEmpty();
        runProvisioningPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReleaseActivation_WhenInnerThrowsAfterAccepted()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            AcceptBeforeThrowing = new InvalidOperationException("pump failed"),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("pump failed");
        activationPort.Releases.Should().BeEmpty();
        runProvisioningPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFallbackWithSameIdsAndClearedTargetSeed_AfterEligibleException()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("auto-run", "definition-auto", ["definition-auto", "auto-run"]),
                    "auto",
                    WorkflowChatRunStartError.None),
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("direct-run", "definition-direct", ["definition-direct", "direct-run"]),
                    "direct",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        inner.Exceptions.Enqueue(new WorkflowDirectFallbackTriggerException("retry direct"));
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-auto", "auto")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        actorResolver.Requests.Should().HaveCount(2);
        actorResolver.Requests[0].TargetSeed.Should().BeNull();
        actorResolver.Requests[1].TargetSeed.Should().BeNull();
        inner.Requests.Should().HaveCount(2);
        inner.Requests[0].TargetSeed!.ActorId.Should().Be("auto-run");
        inner.Requests[1].TargetSeed!.ActorId.Should().Be("direct-run");
        inner.Requests[1].Source.WorkflowName.Should().Be(WorkflowRunBehaviorOptions.DirectWorkflowName);
        inner.Requests[1].Source.ActorId.Should().BeNull();
        inner.Requests[1].CommandIdSeed.Should().Be(inner.Requests[0].CommandIdSeed);
        inner.Requests[1].CorrelationIdSeed.Should().Be(inner.Requests[0].CorrelationIdSeed);
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations[0]);
        runProvisioningPort.DestroyCalls.Should().Equal("auto-run", "definition-auto");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotFallback_WhenOperationIsCanceled()
    {
        var actorResolver = new RecordingActorResolver
        {
            Results =
            {
                new WorkflowActorResolutionResult(
                    new WorkflowRunCreationReceipt("run-1", "definition-1", ["definition-1", "run-1"]),
                    "auto",
                    WorkflowChatRunStartError.None),
            },
        };
        var activationPort = new RecordingActivationPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        inner.Exceptions.Enqueue(new OperationCanceledException("cancelled"));
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            activationPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-auto", "auto")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actorResolver.Requests.Should().ContainSingle();
        inner.Requests.Should().ContainSingle();
        activationPort.Releases.Should().ContainSingle()
            .Which.Should().Be(activationPort.Activations.Single());
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    private static WorkflowChatRunInteractionService CreateService(
        RecordingActorResolver actorResolver,
        RecordingProjectionPort projectionPort,
        RecordingActivationPort activationPort,
        RecordingRunProvisioningPort runProvisioningPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> inner,
        WorkflowDirectFallbackPolicy? fallbackPolicy = null) =>
        new(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            activationPort,
            inner,
            fallbackPolicy ?? new WorkflowDirectFallbackPolicy());

    private static ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> CreateDefaultInner(
        RecordingProjectionPort projectionPort,
        RecordingRunProvisioningPort runProvisioningPort)
    {
        var durableCompletionResolver = new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort());
        var targetResolver = new WorkflowRunCommandTargetResolver(
            new RecordingActorResolver
            {
                Results =
                {
                    new WorkflowActorResolutionResult(
                        new WorkflowRunCreationReceipt("unexpected-run", "unexpected-definition", []),
                        "direct",
                        WorkflowChatRunStartError.None),
                },
            },
            projectionPort,
            runProvisioningPort,
            durableCompletionResolver);
        var receiptFactory = new WorkflowRunAcceptedReceiptFactory();
        var dispatchPipeline = new DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(
            targetResolver,
            new DefaultCommandContextPolicy(),
            new WorkflowChatRequestEnvelopeFactory(),
            new RecordingWorkflowTargetDispatcher(),
            receiptFactory);

        return new DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
            dispatchPipeline,
            new DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>(
                new IdentityEventFrameMapper<WorkflowRunEventEnvelope>()),
            new WorkflowRunCompletionPolicy(),
            new WorkflowRunFinalizeEmitter(new NoopCurrentStateQueryPort()),
            durableCompletionResolver,
            observationLifecycle: new WorkflowRunObservationLifecycle(projectionPort),
            receiptFactory: receiptFactory);
    }

    private sealed class RecordingActorResolver : IWorkflowRunActorResolver
    {
        private int _nextResult;

        public List<WorkflowActorResolutionResult> Results { get; } = [];
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Results[_nextResult++]);
        }
    }

    private sealed class RecordingProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled { get; init; } = true;
        public bool AttachExistingReturnsNull { get; init; }
        public Exception? AttachExistingException { get; init; }
        public List<(string RootActorId, string CommandId)> AttachExistingCalls { get; } = [];
        public List<IAsyncDisposable?> DetachCalls { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseCalls { get; } = [];

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Aevatar.CQRS.Core.Abstractions.Streaming.EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = sink;
            ct.ThrowIfCancellationRequested();
            AttachExistingCalls.Add((rootActorId, commandId));
            if (AttachExistingException != null)
                throw AttachExistingException;

            return Task.FromResult<Aevatar.CQRS.Core.Abstractions.Streaming.EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?>(
                AttachExistingReturnsNull
                    ? null
                    : new Aevatar.CQRS.Core.Abstractions.Streaming.EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(
                        new RecordingWorkflowExecutionProjectionLease(rootActorId, commandId),
                        new NoopAsyncDisposable()));
        }

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DetachCalls.Add(liveSinkLease);
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(IWorkflowExecutionProjectionLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowTargetDispatcher : Aevatar.CQRS.Core.Abstractions.Commands.ICommandTargetDispatcher<WorkflowRunCommandTarget>
    {
        public Task<Aevatar.Foundation.Abstractions.DispatchAdmission> DispatchAsync(
            WorkflowRunCommandTarget target,
            Aevatar.Foundation.Abstractions.EventEnvelope envelope,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("dispatch failed");
    }

    private sealed record RecordingWorkflowExecutionProjectionLease(
        string ActorId,
        string CommandId) : IWorkflowExecutionProjectionLease;

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingActivationPort : IWorkflowChatRunObservationScopeActivationPort
    {
        public bool ReturnNull { get; set; }
        public List<WorkflowChatRunObservationScopeActivation> Activations { get; } = [];
        public List<WorkflowChatRunObservationScopeActivation> Releases { get; } = [];

        public Task<WorkflowChatRunObservationScopeActivation?> ActivateAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ReturnNull)
                return Task.FromResult<WorkflowChatRunObservationScopeActivation?>(null);

            var activation = new WorkflowChatRunObservationScopeActivation(actorId, commandId);
            Activations.Add(activation);
            return Task.FromResult<WorkflowChatRunObservationScopeActivation?>(activation);
        }

        public Task ReleaseAsync(
            WorkflowChatRunObservationScopeActivation activation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Releases.Add(activation);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunProvisioningPort : IWorkflowRunProvisioningPort
    {
        public List<string> DestroyCalls { get; } = [];

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyCalls.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public Queue<Exception> Exceptions { get; } = new();
        public List<WorkflowChatRunRequest> Requests { get; } = [];
        public bool AcceptBeforeReturningFailure { get; init; }
        public Exception? AcceptBeforeThrowing { get; init; }
        public CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>? Result { get; init; }

        Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>.ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Requests.Add(command);
            if (Exceptions.Count > 0)
                throw Exceptions.Dequeue();

            var receipt = new WorkflowChatRunAcceptedReceipt(
                command.TargetSeed?.ActorId ?? "run-1",
                command.TargetSeed?.WorkflowNameForRun ?? command.Source.WorkflowName ?? "direct",
                command.CommandIdSeed ?? "cmd-1",
                command.CorrelationIdSeed ?? "corr-1");
            if (AcceptBeforeThrowing != null && onAcceptedAsync != null)
                return AcceptAndThrowAsync(onAcceptedAsync, receipt, ct, AcceptBeforeThrowing);

            if (AcceptBeforeReturningFailure && onAcceptedAsync != null)
                return AcceptAndReturnAsync(onAcceptedAsync, receipt, ct);

            if (Result != null)
                return Task.FromResult(Result);

            if (onAcceptedAsync != null)
                return AcceptAndReturnAsync(
                    onAcceptedAsync,
                    receipt,
                    ct,
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                        receipt,
                        new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                            WorkflowProjectionCompletionStatus.Completed,
                            true)));

            return Task.FromResult(CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true)));
        }

        async Task<RealtimeSessionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>
            IRealtimeSession<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>.ExecuteAsync(
                WorkflowChatRunRequest inbound,
                Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
                Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ((ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>)this).ExecuteAsync(
                inbound,
                emitAsync,
                onAcceptedAsync,
                ct);
        }

        private async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> AcceptAndReturnAsync(
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask> onAcceptedAsync,
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct,
            CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>? result = null)
        {
            await onAcceptedAsync(receipt, ct);
            return result ?? Result!;
        }

        private static async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> AcceptAndThrowAsync(
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask> onAcceptedAsync,
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct,
            Exception exception)
        {
            await onAcceptedAsync(receipt, ct);
            throw exception;
        }
    }

    private sealed class NoopCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorSnapshot?>(null);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }
}
