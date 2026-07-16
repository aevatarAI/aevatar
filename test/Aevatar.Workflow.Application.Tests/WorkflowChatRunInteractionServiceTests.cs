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
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort { ProjectionEnabled = false },
            new RecordingRunProvisioningPort(),
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorResolver.Requests.Should().BeEmpty();
        inner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnInvalidCallerCredential_BeforeActorResolutionOrActivation()
    {
        var actorResolver = new RecordingActorResolver();
        var projectionPort = new RecordingProjectionPort();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.CatalogWorkflow("direct"),
                CallerCredential: new WorkflowCallerCredential("Bearer token-123")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
        actorResolver.Requests.Should().BeEmpty();
        projectionPort.AttachExistingCalls.Should().BeEmpty();
        projectionPort.DetachCalls.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        inner.Requests.Should().BeEmpty();
        runProvisioningPort.DestroyCalls.Should().BeEmpty();
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
        var inner = new RecordingInteractionService();
        var acceptedReceipts = new List<WorkflowChatRunAcceptedReceipt>();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace"] = "trace-1",
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
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
        inner.Requests.Should().ContainSingle();
        var command = inner.Requests.Single();
        command.TargetSeed.Should().NotBeNull();
        command.TargetSeed!.ActorId.Should().Be("run-1");
        command.TargetSeed.WorkflowNameForRun.Should().Be("direct");
        command.TargetSeed.CreatedActorIds.Should().Equal("definition-1", "run-1");
        command.CommandIdSeed.Should().NotBeNullOrWhiteSpace();
        command.CorrelationIdSeed.Should().NotBeNullOrWhiteSpace();
        command.Headers.Should().BeSameAs(headers);
        acceptedReceipts.Should().ContainSingle();
        acceptedReceipts[0].CommandId.Should().Be(command.CommandIdSeed);
        acceptedReceipts[0].CorrelationId.Should().Be(command.CorrelationIdSeed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPreserveTrustedCallerCommandAndCorrelationSeeds()
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
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            new RecordingRunProvisioningPort(),
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.CatalogWorkflow("direct"),
                CommandIdSeed: "caller-command",
                CorrelationIdSeed: "caller-correlation"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        inner.Requests.Should().ContainSingle();
        inner.Requests[0].CommandIdSeed.Should().Be("caller-command");
        inner.Requests[0].CorrelationIdSeed.Should().Be("caller-correlation");
        result.Receipt!.CommandId.Should().Be("caller-command");
        result.Receipt.CorrelationId.Should().Be("caller-correlation");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReserveAndBindChatHistoryDelivery_WhenIntentIsPresent()
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
        var deliveryPort = new RecordingChatHistoryTerminalDeliveryPort();
        var inner = new RecordingInteractionService();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            new RecordingRunProvisioningPort(),
            inner,
            chatHistoryTerminalDeliveryPort: deliveryPort);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                "execution prompt with transcript",
                WorkflowChatSource.CatalogWorkflow("direct"),
                ScopeId: "scope-a",
                ChatHistory: new WorkflowChatHistoryWriteIntent(
                    "conversation-1",
                    "turn-1",
                    "original user text")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        deliveryPort.Reservations.Should().ContainSingle();
        deliveryPort.Reservations[0].ScopeId.Should().Be("scope-a");
        deliveryPort.Reservations[0].ConversationId.Should().Be("conversation-1");
        deliveryPort.Reservations[0].TurnId.Should().Be("turn-1");
        deliveryPort.Reservations[0].UserText.Should().Be("original user text");
        deliveryPort.Reservations[0].WorkflowActorId.Should().Be("run-1");
        deliveryPort.Reservations[0].WorkflowCommandId.Should().Be(inner.Requests[0].CommandIdSeed);
        var notificationTarget = inner.Requests[0].CompletionNotificationTarget;
        notificationTarget.Should().NotBeNull();
        notificationTarget!.ActorId.Should().Be(deliveryPort.ReservedDeliveryActorId);
        notificationTarget.DeliveryId.Should().Be(deliveryPort.Reservations[0].DeliveryId);
        notificationTarget.ActorId.Should().NotBe(notificationTarget.DeliveryId);
        notificationTarget.ExpiresAtUnixMs.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        deliveryPort.Bindings.Should().ContainSingle();
        deliveryPort.Bindings[0].WorkflowActorId.Should().Be("run-1");
        deliveryPort.Bindings[0].WorkflowCommandId.Should().Be(inner.Requests[0].CommandIdSeed);
        deliveryPort.Abandons.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailBeforeDispatch_WhenChatHistoryReservationIsUnavailable()
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
        var deliveryPort = new RecordingChatHistoryTerminalDeliveryPort
        {
            ReturnNullReservation = true,
        };
        var inner = new RecordingInteractionService();
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            runProvisioningPort,
            inner,
            chatHistoryTerminalDeliveryPort: deliveryPort);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                "execution prompt",
                WorkflowChatSource.CatalogWorkflow("direct"),
                ScopeId: "scope-a",
                ChatHistory: new WorkflowChatHistoryWriteIntent(
                    "conversation-1",
                    "turn-1",
                    "original user text")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        deliveryPort.Reservations.Should().ContainSingle();
        inner.Requests.Should().BeEmpty();
        deliveryPort.Bindings.Should().BeEmpty();
        deliveryPort.Abandons.Should().BeEmpty();
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAbandonChatHistoryDelivery_WhenInnerFailsBeforeAccepted()
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
        var deliveryPort = new RecordingChatHistoryTerminalDeliveryPort();
        var inner = new RecordingInteractionService
        {
            Result = CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.ProjectionUnavailable),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            new RecordingRunProvisioningPort(),
            inner,
            chatHistoryTerminalDeliveryPort: deliveryPort);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.CatalogWorkflow("direct"),
                ScopeId: "scope-a",
                ChatHistory: new WorkflowChatHistoryWriteIntent(
                    "conversation-1",
                    "turn-1",
                    "original user text")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        deliveryPort.Reservations.Should().ContainSingle();
        deliveryPort.Bindings.Should().BeEmpty();
        deliveryPort.Abandons.Should().ContainSingle();
        deliveryPort.Abandons[0].DeliveryId.Should().Be(deliveryPort.Reservations[0].DeliveryId);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollback_WhenInnerReturnsProjectionUnavailableBeforeAccepted()
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            Result = CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.ProjectionUnavailable),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        inner.Requests.Should().ContainSingle();
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollback_WhenInnerFailsBeforeAccepted()
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            Result = CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Failure(WorkflowChatRunStartError.ProjectionUnavailable),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionUnavailable);
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.RootActorId.Should().Be("run-1");
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.RootActorId.Should().Be("run-1");
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = CreateDefaultInner(projectionPort, runProvisioningPort);
        var service = CreateService(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        projectionPort.AttachExistingCalls.Should().ContainSingle()
            .Which.RootActorId.Should().Be("run-1");
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
            runProvisioningPort,
            inner);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService
        {
            AcceptBeforeThrowing = new InvalidOperationException("pump failed"),
        };
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("pump failed");
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        inner.Exceptions.Enqueue(new WorkflowDirectFallbackTriggerException("retry direct"));
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
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
        var runProvisioningPort = new RecordingRunProvisioningPort();
        var inner = new RecordingInteractionService();
        inner.Exceptions.Enqueue(new OperationCanceledException("cancelled"));
        var service = CreateService(
            actorResolver,
            new RecordingProjectionPort(),
            runProvisioningPort,
            inner);

        var act = () => service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-auto", "auto")),
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<OperationCanceledException>();
        actorResolver.Requests.Should().ContainSingle();
        inner.Requests.Should().ContainSingle();
        runProvisioningPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    private static WorkflowChatRunInteractionService CreateService(
        RecordingActorResolver actorResolver,
        RecordingProjectionPort projectionPort,
        RecordingRunProvisioningPort runProvisioningPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> inner,
        WorkflowDirectFallbackPolicy? fallbackPolicy = null,
        IWorkflowChatHistoryTerminalDeliveryPort? chatHistoryTerminalDeliveryPort = null) =>
        new(
            actorResolver,
            projectionPort,
            runProvisioningPort,
            inner,
            fallbackPolicy ?? new WorkflowDirectFallbackPolicy(),
            chatHistoryTerminalDeliveryPort);

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

    private sealed class RecordingChatHistoryTerminalDeliveryPort : IWorkflowChatHistoryTerminalDeliveryPort
    {
        public string ReservedDeliveryActorId { get; } = "chat-history-delivery-actor-alpha";
        public bool ReturnNullReservation { get; init; }
        public List<WorkflowChatHistoryTerminalDeliveryReservationRequest> Reservations { get; } = [];
        public List<WorkflowChatHistoryTerminalDeliveryReservation> Bindings { get; } = [];
        public List<WorkflowChatHistoryTerminalDeliveryReservation> Abandons { get; } = [];

        public Task<WorkflowChatHistoryTerminalDeliveryReservation?> ReserveAsync(
            WorkflowChatHistoryTerminalDeliveryReservationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reservations.Add(request);
            if (ReturnNullReservation)
                return Task.FromResult<WorkflowChatHistoryTerminalDeliveryReservation?>(null);

            return Task.FromResult<WorkflowChatHistoryTerminalDeliveryReservation?>(new WorkflowChatHistoryTerminalDeliveryReservation(
                ReservedDeliveryActorId,
                request.DeliveryId,
                request.WorkflowActorId,
                request.WorkflowCommandId));
        }

        public Task BindAcceptedAsync(
            WorkflowChatHistoryTerminalDeliveryReservation reservation,
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Bindings.Add(reservation);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(
            WorkflowChatHistoryTerminalDeliveryReservation reservation,
            string reason,
            CancellationToken ct = default)
        {
            _ = reason;
            ct.ThrowIfCancellationRequested();
            Abandons.Add(reservation);
            return Task.CompletedTask;
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

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }
}
