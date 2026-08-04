using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.Projection;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRevisionCatalogGAgentTests
{
    [Fact]
    public async Task CreatePreparePublish_ShouldPersistArtifact_AndReplayPublishedState()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var dispatchPort = new RecordingActorDispatchPort();
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1")));
        var agent = CreateAgent(eventStore, adapter, actorId, dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var record = agent.State.Revisions["r1"];
        record.Status.Should().Be(ServiceRevisionStatus.Published);
        record.ArtifactHash.Should().NotBeNullOrWhiteSpace();
        record.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        record.PreparedArtifact.Should().NotBeNull();
        record.PreparedArtifact.RevisionId.Should().Be("r1");
        dispatchPort.Calls.Should().HaveCount(3);
        dispatchPort.Calls.Should().OnlyContain(x =>
            x.ActorId == ServiceActorIds.InvocationCatalog(identity));
        var createObservation = dispatchPort.Calls[0].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        createObservation.SourceRevisionVersion.Should().Be(1);
        createObservation.Revisions.Should().ContainKey("r1")
            .WhoseValue.Status.Should().Be(ServiceRevisionStatus.Created);
        var prepareObservation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        prepareObservation.SourceRevisionVersion.Should().Be(2);
        prepareObservation.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Prepared);
        prepareObservation.Revisions["r1"].PreparedArtifact.Should().NotBeNull();
        prepareObservation.Revisions["r1"].PreparedArtifact.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        var publishObservation = dispatchPort.Calls[2].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        publishObservation.SourceRevisionVersion.Should().Be(3);
        publishObservation.Identity.Should().BeEquivalentTo(identity);
        publishObservation.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Published);

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();
        replayed.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Published);
    }

    [Fact]
    public async Task PrepareAndPublish_ShouldReusePublishedRevision_AndRefreshInvocationObservation()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-published");
        const string revisionId = "rev-published";
        var prepareCalls = 0;
        var adapter = new RecordingAdapter(_ =>
        {
            prepareCalls++;
            if (prepareCalls > 2)
                throw new InvalidOperationException("persisted admission evidence is stale");

            return Task.FromResult(
                GAgentServiceTestKit.CreatePreparedStaticArtifact(
                    identity,
                    revisionId,
                    GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat-readonly")));
        });
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity),
            dispatchPort);

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        prepareCalls.Should().Be(2);
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
        dispatchPort.Calls.Should().HaveCount(5);
        var replayObservation = dispatchPort.Calls[^1].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        replayObservation.SourceRevisionVersion.Should().Be(3);
        replayObservation.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
    }

    [Fact]
    public async Task PreparePublishedWorkflowRevision_ShouldRepairLegacyArtifact_AndPreservePublishedState()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity("svc-legacy-workflow");
        const string revisionId = "rev-legacy-workflow";
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var repairedArtifact = CreateWorkflowArtifact(
            identity,
            revisionId,
            ExternalCapabilityExecutionMode.Interactive);
        var legacyArtifact = repairedArtifact.Clone();
        legacyArtifact.DeploymentPlan.WorkflowPlan.ExecutionMode =
            ExternalCapabilityExecutionMode.Unspecified;
        legacyArtifact = new PreparedServiceRevisionArtifactAssembler().Assemble(legacyArtifact);
        repairedArtifact = new PreparedServiceRevisionArtifactAssembler().Assemble(repairedArtifact);
        var publishedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5));
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(repairedArtifact.Clone()),
            ServiceImplementationKind.Workflow);

        var seed = CreateAgent(eventStore, adapter, actorId);
        await seed.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = CreateWorkflowRevisionSpec(identity, revisionId),
        });
        await eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "legacy-prepared",
                    Version = 2,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-6)),
                    EventData = Any.Pack(new ServiceRevisionPreparedEvent
                    {
                        Identity = identity.Clone(),
                        RevisionId = revisionId,
                        ImplementationKind = ServiceImplementationKind.Workflow,
                        ArtifactHash = legacyArtifact.ArtifactHash,
                        Endpoints = { legacyArtifact.Endpoints.Select(x => x.Clone()) },
                        PreparedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-6)),
                        PreparedArtifact = legacyArtifact.Clone(),
                    }),
                },
                new StateEvent
                {
                    EventId = "legacy-published",
                    Version = 3,
                    Timestamp = publishedAt.Clone(),
                    EventData = Any.Pack(new ServiceRevisionPublishedEvent
                    {
                        Identity = identity.Clone(),
                        RevisionId = revisionId,
                        PublishedAt = publishedAt.Clone(),
                    }),
                },
            ],
            expectedVersion: 1);

        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, adapter, actorId, dispatchPort);
        await agent.ActivateAsync();

        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        var record = agent.State.Revisions[revisionId];
        record.Status.Should().Be(ServiceRevisionStatus.Published);
        record.PublishedAt.Should().Be(publishedAt);
        record.ArtifactHash.Should().Be(repairedArtifact.ArtifactHash);
        record.ArtifactHash.Should().NotBe(legacyArtifact.ArtifactHash);
        record.PreparedArtifact.DeploymentPlan.WorkflowPlan.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        record.PreparedArtifact.DeploymentPlan.WorkflowPlan.DefinitionActorId.Should()
            .Be("workflow-definition-legacy");
        WorkflowServiceDeploymentPlanIntegrity.IsCompatible(
            record.PreparedArtifact,
            revisionId).Should().BeTrue();
        adapter.PrepareCalls.Should().Be(1);
        dispatchPort.Calls.Should().ContainSingle();
        var observation = dispatchPort.Calls[0].Envelope.Payload
            .Unpack<ObserveServiceInvocationRevisionsCommand>();
        observation.SourceRevisionVersion.Should().Be(4);
        observation.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);

        var committedEvents = await eventStore.GetEventsAsync(actorId);
        committedEvents.Should().HaveCount(4);
        var repairedEvent = committedEvents[^1].EventData
            .Unpack<ServiceRevisionPreparedArtifactRepairedEvent>();
        repairedEvent.PreviousArtifactHash.Should().Be(legacyArtifact.ArtifactHash);
        repairedEvent.ArtifactHash.Should().Be(repairedArtifact.ArtifactHash);
        repairedEvent.RepairReason.Should().Be(
            ServiceRevisionPreparedArtifactRepairReason.WorkflowDeploymentPlanIncompatible);

        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        adapter.PrepareCalls.Should().Be(1);
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(4);

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();
        replayed.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
        replayed.State.Revisions[revisionId].ArtifactHash.Should().Be(repairedArtifact.ArtifactHash);
    }

    [Fact]
    public async Task Replay_ShouldPreservePublishedRevision_WhenLegacyDuplicatePrepareRecordedFailure()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity("svc-legacy-replay");
        const string revisionId = "rev-legacy-replay";
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId)));
        var agent = CreateAgent(eventStore, adapter, actorId);

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await eventStore.AppendAsync(
            actorId,
            [new StateEvent
            {
                EventId = "legacy-duplicate-prepare-failed",
                Version = 4,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                EventData = Any.Pack(new ServiceRevisionPreparationFailedEvent
                {
                    Identity = identity.Clone(),
                    RevisionId = revisionId,
                    FailureReason = "persisted admission evidence is stale",
                    OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            }],
            expectedVersion: 3);

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();

        replayed.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
        replayed.State.Revisions[revisionId].FailureReason.Should().BeEmpty();
        replayed.State.LastAppliedEventVersion.Should().Be(4);
    }

    [Fact]
    public async Task RestartAfterPrepare_ShouldReplayPreparedArtifactFromCommittedState()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var deploymentId = ServiceActorIds.Deployment(identity);
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"))));
        var agent = CreateAgent(eventStore, adapter, actorId);
        await agent.ActivateAsync();

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();

        var artifact = replayed.State.Revisions["r1"].PreparedArtifact;
        artifact.Should().NotBeNull();
        artifact.RevisionId.Should().Be("r1");
        artifact.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");

        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        await projector.ProjectAsync(
            new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = "service-revisions",
            },
            new EventEnvelope
            {
                Id = "outer-replayed-prepared",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = replayed.State.LastEventId,
                        Version = replayed.State.LastAppliedEventVersion,
                        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                        EventData = Any.Pack(new ServiceRevisionPreparedEvent
                        {
                            Identity = identity.Clone(),
                            RevisionId = "r1",
                        }),
                    },
                    StateRoot = Any.Pack(replayed.State.Clone()),
                }),
            });

        var reader = new ServiceRevisionCatalogQueryReader(store);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var deployment = CreateDeploymentAgent(reader, activator, dispatchPort, deploymentId);
        await deployment.ActivateAsync();

        await deployment.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        activator.ActivationRequests.Should().ContainSingle();
        activator.ActivationRequests[0].Artifact.RevisionId.Should().Be("r1");
        activator.ActivationRequests[0].Artifact.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].command.Targets.Should().ContainSingle();
        dispatchPort.Commands[0].command.Targets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldPersistPreparationFailure_WhenAdapterThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => throw new InvalidOperationException("prepare failed")),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("prepare failed");
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.PreparationFailed);
        agent.State.Revisions["r1"].FailureReason.Should().Be("prepare failed");
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldNotCommitFailure_WhenPreparedFactPublicationFails()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity("svc-publication-failure");
        const string revisionId = "rev-publication-failure";
        var agent = CreateAgent(
            eventStore,
            new RecordingAdapter(_ => Task.FromResult(
                GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId))),
            ServiceActorIds.RevisionCatalog(identity),
            configureServices: services =>
                services.AddSingleton<ICommittedStatePublicationHook>(new RejectPreparedPublicationHook()));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await act.Should().ThrowAsync<CommittedStatePublicationException>();
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Prepared);
        var committedEvents = await eventStore.GetEventsAsync(ServiceActorIds.RevisionCatalog(identity));
        committedEvents.Should().HaveCount(2);
        committedEvents[^1].EventData.Is(ServiceRevisionPreparedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRequirePreparedRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be prepared before publish*");
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRevalidatePreparedArtifactBeforeCommit()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var prepareCalls = 0;
        var adapter = new RecordingAdapter(_ =>
        {
            prepareCalls++;
            if (prepareCalls == 1)
            {
                return Task.FromResult(
                    GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
            }

            throw new InvalidOperationException("publish admission failed");
        });
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("publish admission failed");
        prepareCalls.Should().Be(2);
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Prepared);
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRejectRevalidatedArtifactHashDrift()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var prepareCalls = 0;
        var adapter = new RecordingAdapter(_ =>
        {
            prepareCalls++;
            var endpoint = GAgentServiceTestKit.CreateEndpointDescriptor(
                endpointId: prepareCalls == 1 ? "run" : "changed");
            return Task.FromResult(
                GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1", endpoint));
        });
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different prepared artifact*");
        prepareCalls.Should().Be(2);
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Prepared);
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectDuplicateRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task HandleRetireRevisionAsync_ShouldPersistRetiredState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await agent.HandleRetireRevisionAsync(new RetireServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Retired);
        agent.State.Revisions["r1"].RetiredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldRejectMissingAdapter()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceRevisionCatalogGAgent, ServiceRevisionCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.RevisionCatalog(identity),
            () => new ServiceRevisionCatalogGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                [],
                new PreparedServiceRevisionArtifactAssembler()));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No service implementation adapter*");
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Created);
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRejectMissingRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = string.Empty,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision_id is required*");
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity("svc-other");
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(otherIdentity, "r2"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    private static ServiceRevisionCatalogGAgent CreateAgent(
        InMemoryEventStore eventStore,
        IServiceImplementationAdapter adapter,
        string actorId,
        IActorDispatchPort? dispatchPort = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceRevisionCatalogGAgent, ServiceRevisionCatalogState>(
            eventStore,
            actorId,
            () => new ServiceRevisionCatalogGAgent(
                dispatchPort ?? GAgentServiceTestKit.NoOpDispatchPort,
                [adapter],
                new PreparedServiceRevisionArtifactAssembler()),
            configureServices);
    }

    private static ServiceRevisionSpec CreateWorkflowRevisionSpec(
        ServiceIdentity identity,
        string revisionId) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                WorkflowName = "legacy-workflow",
                WorkflowYaml = "name: legacy-workflow\nsteps: []",
                DefinitionActorId = "workflow-definition-legacy",
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                {
                    ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
        };

    private static PreparedServiceRevisionArtifact CreateWorkflowArtifact(
        ServiceIdentity identity,
        string revisionId,
        ExternalCapabilityExecutionMode executionMode) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            Endpoints =
            {
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"),
            },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "legacy-workflow",
                    WorkflowYaml = "name: legacy-workflow\nsteps: []",
                    DefinitionActorId = "workflow-definition-legacy",
                    ExecutionMode = executionMode,
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    },
                },
            },
        };

    private static ServiceDeploymentManagerGAgent CreateDeploymentAgent(
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        RecordingRuntimeActivator activator,
        RecordingDispatchPort dispatchPort,
        string actorId)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceDeploymentManagerGAgent, ServiceDeploymentState>(
            new InMemoryEventStore(),
            actorId,
            () => new ServiceDeploymentManagerGAgent(
                dispatchPort,
                revisionCatalogQueryReader,
                new AlwaysReadyCapabilityViewReader(),
                new AllowActivationAdmissionEvaluator(),
                activator));
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, ReplaceResolvedServiceServingTargetsCommand command)> Commands { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Commands.Add((actorId, envelope.Payload.Unpack<ReplaceResolvedServiceServingTargetsCommand>()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class AlwaysReadyCapabilityViewReader : IActivationCapabilityViewReader
    {
        public Task<ActivationCapabilityView> GetAsync(
            ServiceIdentity identity,
            string revisionId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ActivationCapabilityView
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
            });
        }
    }

    private sealed class AllowActivationAdmissionEvaluator : IActivationAdmissionEvaluator
    {
        public Task<ActivationAdmissionDecision> EvaluateAsync(
            ActivationAdmissionRequest request,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ActivationAdmissionDecision
            {
                Allowed = true,
            });
        }
    }

    private sealed class RecordingRuntimeActivator : IServiceRuntimeActivator
    {
        public Queue<ServiceRuntimeActivationResult> ActivationResults { get; } = new();

        public List<ServiceRuntimeActivationRequest> ActivationRequests { get; } = [];

        public Task<ServiceRuntimeActivationResult> ActivateAsync(
            ServiceRuntimeActivationRequest request,
            CancellationToken ct = default)
        {
            ActivationRequests.Add(request);
            if (ActivationResults.Count == 0)
                throw new InvalidOperationException("No activation result configured.");

            return Task.FromResult(ActivationResults.Dequeue());
        }

        public Task DeactivateAsync(ServiceRuntimeDeactivationRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingAdapter : IServiceImplementationAdapter
    {
        private readonly Func<PrepareServiceRevisionRequest, Task<PreparedServiceRevisionArtifact>> _prepare;
        private readonly ServiceImplementationKind _implementationKind;

        public RecordingAdapter(
            Func<PrepareServiceRevisionRequest, Task<PreparedServiceRevisionArtifact>> prepare,
            ServiceImplementationKind implementationKind = ServiceImplementationKind.Static)
        {
            _prepare = prepare;
            _implementationKind = implementationKind;
        }

        public int PrepareCalls { get; private set; }

        public ServiceImplementationKind ImplementationKind => _implementationKind;

        public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
            PrepareServiceRevisionRequest request,
            CancellationToken ct = default)
        {
            PrepareCalls++;
            return _prepare(request);
        }
    }

    private sealed class RejectPreparedPublicationHook : ICommittedStatePublicationHook
    {
        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Published.StateEvent?.EventData?.Is(ServiceRevisionPreparedEvent.Descriptor) == true)
                throw new InvalidOperationException("prepared fact projection unavailable");

            return Task.CompletedTask;
        }
    }
}
