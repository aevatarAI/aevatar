using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDeploymentManagerGAgentTests
{
    [Fact]
    public async Task HandleActivateAsync_ShouldPersistAndReplayDeploymentRecord()
    {
        var eventStore = new InMemoryEventStore();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            artifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var actorId = ServiceActorIds.Deployment(identity);
        var agent = CreateAgent(eventStore, revisionCatalog, activator, actorId);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].RevisionId.Should().Be("r1");
        agent.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");
        agent.State.Deployments["dep-r1"].ArtifactHash.Should().Be(artifact.ArtifactHash);

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, revisionCatalog, activator, actorId);
        await replayed.ActivateAsync();
        replayed.State.Deployments.Should().ContainKey("dep-r1");
        replayed.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");
        replayed.State.Deployments["dep-r1"].ArtifactHash.Should().Be(artifact.ArtifactHash);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldDispatchResolvedServingTargetsAfterActivation()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity), dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].actorId.Should().Be(ServiceActorIds.ServingSet(identity));
        dispatchPort.Commands[0].command.Targets.Should().ContainSingle();
        dispatchPort.Commands[0].command.Targets[0].DeploymentId.Should().Be("dep-r1");
        dispatchPort.Commands[0].command.Targets[0].RevisionId.Should().Be("r1");
        dispatchPort.Commands[0].command.Targets[0].PrimaryActorId.Should().Be("actor-r1");
        dispatchPort.Commands[0].command.Targets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldDispatchResolvedServingTargets_WhenRevisionIsAlreadyActive()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity), dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        activator.ActivationRequests.Should().ContainSingle();
        dispatchPort.Commands.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRevalidateRuntimeAndReplayArtifactHash_WhenActiveDeploymentPredatesHashFence()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Deployment(identity);
        var activatedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1));
        await eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "legacy-activated-without-artifact-hash",
                    Version = 1,
                    Timestamp = activatedAt.Clone(),
                    EventData = Any.Pack(ParseLegacyActivatedWire(
                        identity,
                        "dep-r1",
                        "r1",
                        "actor-r1",
                        activatedAt)),
                },
            ],
            expectedVersion: 0);
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            artifact);
        var dispatchPort = new RecordingDispatchPort();
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            activator,
            actorId,
            dispatchPort);
        await agent.ActivateAsync();

        agent.State.Deployments["dep-r1"].ArtifactHash.Should().BeEmpty();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-bind-legacy-hash",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        activator.ActivationRequests.Should().ContainSingle();
        activator.ActivationRequests[0].Artifact.ArtifactHash.Should().Be(artifact.ArtifactHash);
        agent.State.Deployments["dep-r1"].ArtifactHash.Should().Be(artifact.ArtifactHash);
        dispatchPort.Commands.Should().ContainSingle();
        (await eventStore.GetEventsAsync(actorId)).Should().Contain(evt =>
            evt.EventData.Is(ServiceDeploymentActivatedEvent.Descriptor) &&
            string.Equals(
                evt.EventData.Unpack<ServiceDeploymentActivatedEvent>().ArtifactHash,
                artifact.ArtifactHash,
                StringComparison.Ordinal));
        await agent.DeactivateAsync();

        var replayed = CreateAgent(
            eventStore,
            revisionCatalog,
            new RecordingRuntimeActivator(),
            actorId,
            new RecordingDispatchPort());
        await replayed.ActivateAsync();

        replayed.State.Deployments["dep-r1"].ArtifactHash.Should().Be(artifact.ArtifactHash);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectActiveDeploymentReuse_WhenArtifactHashDiffers()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var originalArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            originalArtifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-original",
            ExpectedArtifactHash = originalArtifact.ArtifactHash,
        });
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        var changedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(
                requestTypeUrl: "type.googleapis.com/test.changed-command"));
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            changedArtifact);
        var act = () => agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-changed",
            ExpectedArtifactHash = changedArtifact.ArtifactHash,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact hash does not match*");
        activator.ActivationRequests.Should().ContainSingle();
        dispatchPort.Commands.Should().ContainSingle();
        agent.State.Deployments["dep-r1"].ArtifactHash.Should().Be(originalArtifact.ArtifactHash);
    }

    [Fact]
    public async Task HandleActivateAsync_FreshAttemptForActiveRevision_ShouldStillRequireAdmission()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var actorId = ServiceActorIds.Deployment(identity);
        var initialActivator = new RecordingRuntimeActivator();
        initialActivator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var initial = CreateAgent(eventStore, revisionCatalog, initialActivator, actorId);
        await initial.ActivateAsync();
        await initial.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-initial",
        });
        await initial.DeactivateAsync();

        var admissionEvaluator = new RejectActivationAdmissionEvaluator();
        var replayActivator = new RecordingRuntimeActivator();
        var replayDispatch = new RecordingDispatchPort();
        var replayed = CreateAgent(
            eventStore,
            revisionCatalog,
            replayActivator,
            actorId,
            replayDispatch,
            admissionEvaluator: admissionEvaluator);
        await replayed.ActivateAsync();

        await replayed.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-active-admission-rejected",
        });

        admissionEvaluator.RequestCount.Should().Be(1);
        replayActivator.ActivationRequests.Should().BeEmpty();
        replayDispatch.Commands.Should().BeEmpty();
        replayed.State.PendingActivations.Should().NotContainKey("r1");
        var failure = replayed.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.FailureCode.Should().Be(ServiceDeploymentActivationFailureCode.AdmissionRejected);
        failure.ActivationAttemptId.Should().Be("attempt-active-admission-rejected");
        replayed.State.Deployments.Should().ContainSingle();
        var active = replayed.State.Deployments["dep-r1"];
        active.RevisionId.Should().Be("r1");
        active.PrimaryActorId.Should().Be("actor-r1");
        active.Status.Should().Be(ServiceDeploymentStatus.Active);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldKeepMultipleActiveDeploymentsForDifferentRevisions()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r2", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r2"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r2", "actor-r2", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity));

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        activator.DeactivateRequests.Should().BeEmpty();
        agent.State.Deployments.Keys.Should().BeEquivalentTo(["dep-r1", "dep-r2"]);
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.Deployments["dep-r2"].Status.Should().Be(ServiceDeploymentStatus.Active);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldBeIdempotentForActiveRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity));

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        activator.ActivationRequests.Should().ContainSingle();
        agent.State.Deployments.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldDispatchFencedServingRemovalBeforeRuntimeDeactivation()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var activator = new RecordingRuntimeActivator();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        BindActiveDeployment(agent, identity);

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });

        activator.DeactivateRequests.Should().BeEmpty();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        var removal = dispatchPort.RemovalCommands.Should().ContainSingle().Subject;
        removal.actorId.Should().Be(ServiceActorIds.ServingSet(identity));
        removal.command.DeploymentId.Should().Be("dep-r1");
        removal.command.RevisionId.Should().Be("r1");
        removal.command.PrimaryActorId.Should().Be("actor-r1");
        removal.command.ActivationAttemptId.Should().Be("attempt-r1");
        removal.command.ServingTargetOperationId.Should().Be("operation-r1");
        removal.command.ReplyActorId.Should().Be(agent.Id);
    }

    [Fact]
    public async Task HandleServingTargetsRemovedAsync_ShouldDeactivateOnlyForMatchingServingAck()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var activator = new RecordingRuntimeActivator();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            activator,
            ServiceActorIds.Deployment(identity));
        await agent.ActivateAsync();
        BindActiveDeployment(agent, identity);
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        var deactivationOperationId = agent.State.PendingDeactivations["dep-r1"]
            .DeactivationOperationId;

        await agent.HandleEventAsync(CreateRemovedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            CreateRemovedAck(
                identity,
                operationId: "operation-old",
                deactivationOperationId: deactivationOperationId)));
        await agent.HandleEventAsync(CreateRemovedAckEnvelope(
            agent.Id,
            "foreign-serving-set",
            CreateRemovedAck(identity, deactivationOperationId: deactivationOperationId)));

        activator.DeactivateRequests.Should().BeEmpty();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);

        await agent.HandleEventAsync(CreateRemovedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            CreateRemovedAck(identity, deactivationOperationId: deactivationOperationId)));

        var request = activator.DeactivateRequests.Should().ContainSingle().Subject;
        request.DeploymentId.Should().Be("dep-r1");
        request.RevisionId.Should().Be("r1");
        request.PrimaryActorId.Should().Be("actor-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task LateRemovalAck_ShouldNotDeactivateDeploymentReusedByNewActivation()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var activator = new RecordingRuntimeActivator();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        BindActiveDeployment(agent, identity);
        agent.State.Deployments["dep-r1"].ArtifactHash = artifact.ArtifactHash;

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        var staleRemoval = dispatchPort.RemovalCommands.Should().ContainSingle().Subject.command;

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-new",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        agent.State.PendingDeactivations.Should().BeEmpty();
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be("attempt-new");
        dispatchPort.Commands.Should().ContainSingle();
        var replacement = dispatchPort.Commands.Single().command;
        replacement.Targets.Single().DeploymentId.Should().Be("dep-r1");
        replacement.ActivationAttemptId.Should().Be("attempt-new");
        replacement.OperationId.Should().NotBe(staleRemoval.ServingTargetOperationId);

        await agent.HandleEventAsync(CreateRemovedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            new ServiceServingTargetsRemovedAck
            {
                Identity = identity.Clone(),
                DeploymentId = staleRemoval.DeploymentId,
                RevisionId = staleRemoval.RevisionId,
                PrimaryActorId = staleRemoval.PrimaryActorId,
                ActivationAttemptId = staleRemoval.ActivationAttemptId,
                ServingTargetOperationId = staleRemoval.ServingTargetOperationId,
                DeactivationOperationId = staleRemoval.DeactivationOperationId,
                Disposition = ServiceServingTargetRemovalDisposition.Removed,
                RemovedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }));

        activator.DeactivateRequests.Should().BeEmpty();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.PendingActivations.Should().ContainKey("r1");

        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        activator.ActivationRequests.Should().BeEmpty(
            "the already-active deployment is reused behind the new activation fence");
        agent.State.PendingActivations.Should().BeEmpty();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.Deployments["dep-r1"].ActivationAttemptId.Should().Be("attempt-new");
        agent.State.Deployments["dep-r1"].ServingTargetOperationId.Should().Be(replacement.OperationId);
    }

    [Fact]
    public async Task RuntimeDeactivationFailure_ShouldResumeDurablyWithSameOperationAfterReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var firstActivator = new RecordingRuntimeActivator();
        firstActivator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        firstActivator.DeactivationExceptions.Enqueue(
            new InvalidOperationException("synthetic runtime deactivation outage"));
        var firstDispatch = new RecordingDispatchPort();
        var firstScheduler = new RecordingCallbackScheduler();
        var actorId = ServiceActorIds.Deployment(identity);
        var first = CreateAgent(
            eventStore,
            revisionCatalog,
            firstActivator,
            actorId,
            firstDispatch,
            firstScheduler);
        await first.ActivateAsync();
        await first.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-r1",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });
        await AcknowledgeLatestServingDispatchAsync(first, identity, firstDispatch);

        await first.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        await AcknowledgeLatestRemovalDispatchAsync(first, identity, firstDispatch);

        var failedRequest = firstActivator.DeactivateRequests.Should().ContainSingle().Subject;
        var pending = first.State.PendingDeactivations["dep-r1"];
        pending.Phase.Should().Be(ServiceDeploymentDeactivationPhase.RuntimeDeactivationPending);
        pending.DeactivationOperationId.Should().Be(failedRequest.DeactivationOperationId);
        first.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        await first.DeactivateAsync();

        var recoveredActivator = new RecordingRuntimeActivator();
        var recoveredDispatch = new RecordingDispatchPort();
        var recoveredScheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(
            eventStore,
            revisionCatalog,
            recoveredActivator,
            actorId,
            recoveredDispatch,
            recoveredScheduler);
        await recovered.ActivateAsync();
        var continuation = recoveredScheduler.ScheduledTimeouts
            .Select(timeout => timeout.Payload)
            .Where(payload => payload.Is(DeactivateServiceDeploymentCommand.Descriptor))
            .Select(payload => payload.Unpack<DeactivateServiceDeploymentCommand>())
            .Should().ContainSingle().Subject;

        continuation.DeactivationOperationId.Should().Be(failedRequest.DeactivationOperationId);
        await recovered.HandleDeactivateAsync(continuation);

        recoveredDispatch.RemovalCommands.Should().BeEmpty(
            "the durable runtime phase must not repeat serving-target removal");
        var recoveredRequest = recoveredActivator.DeactivateRequests.Should().ContainSingle().Subject;
        recoveredRequest.DeactivationOperationId.Should().Be(failedRequest.DeactivationOperationId);
        recovered.State.PendingDeactivations.Should().BeEmpty();
        recovered.State.DeactivationFailures.Should().BeEmpty();
        recovered.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task DeactivationRetrySchedulingFailure_ShouldRemainRetryableAfterPendingCommit()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        scheduler.ScheduleTimeoutExceptions.Enqueue(
            new InvalidOperationException("synthetic deactivation scheduler outage"));
        var actorId = ServiceActorIds.Deployment(identity);
        var first = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            actorId,
            scheduler: scheduler);
        await first.ActivateAsync();
        BindActiveDeployment(first, identity);

        var act = () => first.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });

        var failure = await act.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        first.State.PendingDeactivations.Should().ContainKey("dep-r1");
        (await eventStore.GetEventsAsync(actorId)).Count(evt =>
                evt.EventData.Is(ServiceDeploymentDeactivationRequestedEvent.Descriptor))
            .Should().Be(1, "the pending deactivation was durable before scheduling failed");

        await first.DeactivateAsync();
        var recoveredScheduler = new RecordingCallbackScheduler();
        var recoveredDispatch = new RecordingDispatchPort();
        var recovered = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            actorId,
            recoveredDispatch,
            recoveredScheduler);

        await recovered.ActivateAsync();

        var continuation = recoveredScheduler.ScheduledTimeouts.Should().ContainSingle().Subject.Payload
            .Unpack<DeactivateServiceDeploymentCommand>();
        await recovered.HandleDeactivateAsync(continuation);
        recoveredDispatch.RemovalCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task TerminalDeactivationFailure_ShouldKeepDeploymentActiveForFreshCleanupAttempt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler,
            timeProvider: clock);
        await agent.ActivateAsync();
        BindActiveDeployment(agent, identity);

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        var firstOperationId = agent.State.PendingDeactivations["dep-r1"]
            .DeactivationOperationId;
        var expiredContinuation = scheduler.ScheduledTimeouts.Single().Payload
            .Unpack<DeactivateServiceDeploymentCommand>();
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(6));

        await agent.HandleDeactivateAsync(expiredContinuation);

        agent.State.PendingDeactivations.Should().BeEmpty();
        agent.State.DeactivationFailures.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });

        var retry = agent.State.PendingDeactivations.Should().ContainKey("dep-r1").WhoseValue;
        retry.DeactivationOperationId.Should().NotBe(firstOperationId);
        agent.State.DeactivationFailures.Should().BeEmpty();
        dispatchPort.RemovalCommands.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldTolerateProjectionLag_ByReArmingInsteadOfThrowing()
    {
        // The bind chain dispatches prepare->publish->activate fire-and-forget, so the revision-catalog
        // projection can lag behind the committed prepare event when activation runs. Activation must NOT
        // fail terminally; it must re-arm a bounded self-continuation until the prepared artifact appears.
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var scheduler = new RecordingCallbackScheduler();
        var actorId = ServiceActorIds.Deployment(identity);
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, actorId, dispatchPort, scheduler);
        await agent.ActivateAsync();
        const string activationAttemptId = "attempt-projection-lag";

        // Projection not yet materialized -> tolerated, re-armed (no throw, no serving-set write yet).
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
        });

        activator.ActivationRequests.Should().BeEmpty("activation must wait until the prepared artifact is visible");
        dispatchPort.Commands.Should().BeEmpty("serving set must not be written before activation succeeds");
        scheduler.ScheduledTimeouts.Should().ContainSingle("activation should re-arm a bounded self-continuation");
        var rearmed = scheduler.ScheduledTimeouts[0].Payload.Unpack<ActivateServiceRevisionCommand>();
        rearmed.RevisionId.Should().Be("r1");
        rearmed.ActivationAttemptId.Should().Be(activationAttemptId);
        rearmed.ActivationDeadlineAt.Should().NotBeNull("the bounded retry deadline must be stamped onto the re-armed command");
        agent.State.PendingActivations.Should().ContainKey("r1");
        agent.State.PendingActivations["r1"].DeadlineAt.Should().Be(rearmed.ActivationDeadlineAt);
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be(activationAttemptId);

        // Projection catches up; the re-fired continuation now succeeds and writes the serving set.
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));

        await agent.HandleActivateAsync(rearmed);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        activator.ActivationRequests.Should().ContainSingle();
        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.PendingActivations.Should().NotContainKey("r1");
        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].actorId.Should().Be(ServiceActorIds.ServingSet(identity));
        dispatchPort.Commands[0].command.Targets[0].DeploymentId.Should().Be("dep-r1");
    }

    [Fact]
    public async Task HandleActivateAsync_WhenRetrySchedulingFailsAfterPendingCommit_ShouldRequireRedeliveryAndRecover()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var firstScheduler = new RecordingCallbackScheduler();
        firstScheduler.ScheduleTimeoutExceptions.Enqueue(
            new InvalidOperationException("synthetic scheduler outage"));
        var actorId = ServiceActorIds.Deployment(identity);
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-scheduler-redelivery",
        };
        var first = CreateAgent(
            eventStore,
            revisionCatalog,
            new RecordingRuntimeActivator(),
            actorId,
            scheduler: firstScheduler);
        await first.ActivateAsync();

        var act = () => first.HandleActivateAsync(command);

        var failure = await act.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        first.State.PendingActivations.Should().ContainKey("r1");
        first.State.PendingActivations["r1"].ActivationAttemptId.Should()
            .Be("attempt-scheduler-redelivery");
        (await eventStore.GetEventsAsync(actorId)).Count(evt =>
                evt.EventData.Is(ServiceDeploymentActivationDeferredEvent.Descriptor))
            .Should().Be(1, "the pending activation was durable before scheduling failed");
        await first.DeactivateAsync();

        var recoveredScheduler = new RecordingCallbackScheduler();
        var recoveredActivator = new RecordingRuntimeActivator();
        recoveredActivator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var recovered = CreateAgent(
            eventStore,
            revisionCatalog,
            recoveredActivator,
            actorId,
            dispatchPort,
            recoveredScheduler);
        await recovered.ActivateAsync();

        recoveredScheduler.ScheduledTimeouts.Should().ContainSingle(
            "activation recovery must re-arm the committed pending record");
        await recovered.HandleActivateAsync(command);
        await AcknowledgeLatestServingDispatchAsync(recovered, identity, dispatchPort);

        recoveredActivator.ActivationRequests.Should().ContainSingle();
        recovered.State.PendingActivations.Should().NotContainKey("r1");
        recovered.State.ActivationFailures.Should().BeEmpty();
        recovered.State.ActivationCompletions.Should().ContainSingle();
        (await eventStore.GetEventsAsync(actorId)).Count(evt =>
                evt.EventData.Is(ServiceDeploymentActivationDeferredEvent.Descriptor))
            .Should().Be(1, "redelivery must reuse the original pending checkpoint");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldReArmUntilServiceCatalogProjectionCatchesUp()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var capabilityViewReader = new DeferredCapabilityViewReader();
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler: scheduler,
            capabilityViewReader: capabilityViewReader);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        capabilityViewReader.RequestCount.Should().Be(1);
        activator.ActivationRequests.Should().BeEmpty();
        scheduler.ScheduledTimeouts.Should().ContainSingle();
        agent.State.PendingActivations.Should().ContainKey("r1");

        capabilityViewReader.IsReady = true;
        var rearmed = scheduler.ScheduledTimeouts[0].Payload.Unpack<ActivateServiceRevisionCommand>();
        await agent.HandleActivateAsync(rearmed);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        capabilityViewReader.RequestCount.Should().Be(2);
        activator.ActivationRequests.Should().ContainSingle();
        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.PendingActivations.Should().NotContainKey("r1");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitCapabilityViewFailure_WhenServiceCatalogLagExceedsDeadline()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler,
            capabilityViewReader: new DeferredCapabilityViewReader());
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-capability-timeout",
        });
        var callback = ExpirePendingActivation(agent, scheduler, "r1");

        await agent.HandleActivateAsync(callback);

        agent.State.ActivationFailures["r1"].FailureCode
            .Should().Be(ServiceDeploymentActivationFailureCode.CapabilityViewNotReady);
        agent.State.ActivationFailures["r1"].FailureReason.Should().Contain("service-catalog projection");
        agent.State.ActivationFailures["r1"].ActivationAttemptId.Should().Be("attempt-capability-timeout");
        scheduler.ScheduledTimeouts.Should().ContainSingle("an exhausted callback must not schedule another retry");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldReuseActorOwnedDeadlineAcrossRecoveryAndMatchingContinuation()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var firstScheduler = new RecordingCallbackScheduler();
        var actorId = ServiceActorIds.Deployment(identity);
        const string activationAttemptId = "attempt-recovered-pending";
        const string expectedArtifactHash = "HASH-RECOVERED";
        var agent = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            actorId,
            scheduler: firstScheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
            ExpectedArtifactHash = expectedArtifactHash,
        });

        var originalDeadline = agent.State.PendingActivations["r1"].DeadlineAt.Clone();
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be(activationAttemptId);
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be(expectedArtifactHash);
        var committedVersion = agent.State.LastAppliedEventVersion;
        await agent.DeactivateAsync();

        var replayScheduler = new RecordingCallbackScheduler();
        var replayed = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            actorId,
            scheduler: replayScheduler);
        await replayed.ActivateAsync();
        replayed.State.PendingActivations["r1"].DeadlineAt.Should().Be(originalDeadline);
        replayed.State.PendingActivations["r1"].ActivationAttemptId.Should().Be(activationAttemptId);
        replayed.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be(expectedArtifactHash);

        await replayed.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationDeadlineAt = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
            ActivationAttemptId = activationAttemptId,
            ExpectedArtifactHash = expectedArtifactHash,
        });

        replayed.State.LastAppliedEventVersion.Should().Be(committedVersion, "a continuation must not replace actor-owned pending state");
        replayed.State.PendingActivations["r1"].DeadlineAt.Should().Be(originalDeadline);
        replayScheduler.ScheduledTimeouts.Should().HaveCount(
            2,
            "activation repairs the lease on recovery and replaces it before each handler attempt");
        replayScheduler.ScheduledTimeouts
            .Select(x => x.Payload.Unpack<ActivateServiceRevisionCommand>())
            .Should().AllSatisfy(callback =>
            {
                callback.ActivationDeadlineAt.Should().Be(originalDeadline);
                callback.ActivationAttemptId.Should().Be(activationAttemptId);
                callback.ExpectedArtifactHash.Should().Be(expectedArtifactHash);
            });
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldPersistArtifactHashUpgradeForSamePendingAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-hash-fence",
            ExpectedArtifactHash = string.Empty,
        };
        await agent.HandleActivateAsync(command);
        var committedVersion = agent.State.LastAppliedEventVersion;

        command.ExpectedArtifactHash = "HASH-NEXT";
        await agent.HandleActivateAsync(command);

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion + 1);
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be("HASH-NEXT");
        scheduler.ScheduledTimeouts[^1].Payload.Unpack<ActivateServiceRevisionCommand>()
            .ExpectedArtifactHash.Should().Be("HASH-NEXT");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectArtifactHashUpgradeAfterServingDispatchAdvanced()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-late-hash-fence",
        };
        await agent.HandleActivateAsync(command);
        var servingCommand = dispatchPort.Commands.Should().ContainSingle().Which.command;
        await agent.HandleServingTargetsAppliedAsync(CreateAppliedAck(
            identity,
            servingCommand.Targets.Single(),
            servingCommand));
        agent.State.PendingActivations["r1"].Phase.Should()
            .Be(ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending);

        command.ExpectedArtifactHash = artifact.ArtifactHash;
        var act = () => agent.HandleActivateAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot add an expected artifact hash after artifact validation has completed*");
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().BeEmpty();
        dispatchPort.DefaultCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectArtifactHashDriftForSamePendingAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-hash-fence",
            ExpectedArtifactHash = "HASH-A",
        };
        await agent.HandleActivateAsync(command);
        var committedVersion = agent.State.LastAppliedEventVersion;

        command.ExpectedArtifactHash = "HASH-B";
        var act = () => agent.HandleActivateAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to a different expected artifact hash*");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be("HASH-A");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldKeepMatchingArtifactHashReplayIdempotent()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-hash-idempotent",
            ExpectedArtifactHash = "HASH-STABLE",
        };
        await agent.HandleActivateAsync(command);
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleActivateAsync(command.Clone());

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be("HASH-STABLE");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectArtifactHashDriftForCompletedAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-completed-hash",
            ExpectedArtifactHash = artifact.ArtifactHash,
        };
        await agent.HandleActivateAsync(command);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        var completion = agent.State.ActivationCompletions.Values.Should().ContainSingle().Subject;
        completion.ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        var committedVersion = agent.State.LastAppliedEventVersion;

        command.ExpectedArtifactHash = "DIFFERENT-HASH";
        var act = () => agent.HandleActivateAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to a different expected artifact hash*");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldFailClosed_WhenCompletedLegacyAttemptIsReplayedWithArtifactHash()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-completed-legacy",
        };
        await agent.HandleActivateAsync(command);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        agent.State.ActivationCompletions.Values.Should().ContainSingle()
            .Which.ExpectedArtifactHash.Should().BeEmpty();

        command.ExpectedArtifactHash = artifact.ArtifactHash;
        var act = () => agent.HandleActivateAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to a different expected artifact hash*");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldKeepMatchingCompletedArtifactHashReplayIdempotent()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-completed-stable",
            ExpectedArtifactHash = artifact.ArtifactHash,
        };
        await agent.HandleActivateAsync(command);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleActivateAsync(command.Clone());

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        activator.ActivationRequests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ServiceRevisionStatus.Created)]
    [InlineData(ServiceRevisionStatus.Prepared)]
    public async Task HandleActivateAsync_ShouldWaitForPublishedRevision(
        ServiceRevisionStatus status)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        artifact.ArtifactHash = "HASH-PENDING-PUBLISH";
        var activator = new RecordingRuntimeActivator();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new SnapshotRevisionCatalogQueryReader(CreateRevisionSnapshot(
                artifact,
                status,
                artifact.ArtifactHash)),
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = $"attempt-{status}",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        activator.ActivationRequests.Should().BeEmpty();
        var pending = agent.State.PendingActivations.Should().ContainKey("r1").WhoseValue;
        pending.LastRetryFailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        pending.ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        scheduler.ScheduledTimeouts.Should().ContainSingle();
        scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>()
            .ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldNotActivatePublishedRevision_WhenExpectedArtifactHashDiffers()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var activator = new RecordingRuntimeActivator();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new SnapshotRevisionCatalogQueryReader(CreateRevisionSnapshot(
                artifact,
                ServiceRevisionStatus.Published,
                artifact.ArtifactHash)),
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-hash-mismatch",
            ExpectedArtifactHash = "HASH-EXPECTED",
        });

        activator.ActivationRequests.Should().BeEmpty();
        agent.State.Deployments.Should().BeEmpty();
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures["r1"].FailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>()
            .ExpectedArtifactHash.Should().Be("HASH-EXPECTED");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldNotActivatePublishedRevision_WhenSnapshotArtifactHashDiffers()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var activator = new RecordingRuntimeActivator();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new SnapshotRevisionCatalogQueryReader(CreateRevisionSnapshot(
                artifact,
                ServiceRevisionStatus.Published,
                "HASH-SNAPSHOT")),
            activator,
            ServiceActorIds.Deployment(identity));
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-snapshot-hash-mismatch",
        });

        activator.ActivationRequests.Should().BeEmpty();
        agent.State.Deployments.Should().BeEmpty();
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures["r1"].FailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldFailImmediately_WhenPublishedArtifactIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var publishedWithoutArtifact = CreateRevisionSnapshot(
            artifact,
            ServiceRevisionStatus.Published,
            artifact.ArtifactHash) with
        {
            PreparedArtifact = null,
        };
        var activator = new RecordingRuntimeActivator();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new SnapshotRevisionCatalogQueryReader(publishedWithoutArtifact),
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-published-artifact-missing",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        activator.ActivationRequests.Should().BeEmpty();
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures["r1"].FailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        agent.State.ActivationFailures["r1"].FailureReason.Should().Contain("integrity");
        scheduler.ScheduledTimeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldPreserveMatchingArtifactFenceThroughServingCheckpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new SnapshotRevisionCatalogQueryReader(CreateRevisionSnapshot(
                artifact,
                ServiceRevisionStatus.Published,
                artifact.ArtifactHash)),
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-matching-hash",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        activator.ActivationRequests.Should().ContainSingle();
        agent.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>()
            .ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitTerminalFailure_WhenProjectionLagExceedsDeadline()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        const string activationAttemptId = "attempt-projection-timeout";
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
        });
        var command = ExpirePendingActivation(agent, scheduler, "r1");

        await agent.HandleActivateAsync(command);

        agent.State.ActivationFailures.Should().ContainKey("r1");
        var failure = agent.State.ActivationFailures["r1"];
        failure.FailureCode.Should().Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        failure.FailureReason.Should().Contain("was not found before the activation deadline");
        failure.OccurredAt.Should().NotBeNull();
        failure.ActivationAttemptId.Should().Be(activationAttemptId);
        scheduler.ScheduledTimeouts.Should().ContainSingle("an exhausted budget must not keep re-arming");

        var committedVersion = agent.State.LastAppliedEventVersion;
        await agent.HandleActivateAsync(command);
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion, "duplicate callbacks must converge on the committed failure");

        await agent.DeactivateAsync();
        var replayed = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));
        await replayed.ActivateAsync();
        replayed.State.ActivationFailures["r1"].FailureCode
            .Should().Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        replayed.State.ActivationFailures["r1"].ActivationAttemptId.Should().Be(activationAttemptId);
    }

    [Fact]
    public async Task HandleActivateAsync_FreshAttemptAfterTerminalFailure_ShouldRearmAndSupersedeFailure()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-a",
        });
        var callbackA = ExpirePendingActivation(agent, scheduler, "r1");
        await agent.HandleActivateAsync(callbackA);
        agent.State.ActivationFailures.Should().ContainKey("r1");
        agent.State.ActivationFailures["r1"].ActivationAttemptId.Should().Be("attempt-a");

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-b",
        });

        agent.State.ActivationFailures.Should().NotContainKey("r1");
        agent.State.PendingActivations.Should().ContainKey("r1");
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be("attempt-b");
        scheduler.ScheduledTimeouts.Should().HaveCount(2);
        scheduler.ScheduledTimeouts[^1].Payload.Unpack<ActivateServiceRevisionCommand>()
            .ActivationAttemptId.Should().Be("attempt-b");
    }

    [Fact]
    public async Task HandleActivateAsync_NewAttemptShouldSupersedePendingAndDiscardOldCallback()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-a",
        });
        var callbackA = scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>();
        var deadlineA = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-10));
        agent.State.PendingActivations["r1"].DeadlineAt = deadlineA;

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-b",
        });

        var pendingB = agent.State.PendingActivations["r1"];
        pendingB.ActivationAttemptId.Should().Be("attempt-b");
        pendingB.DeadlineAt.Should().NotBe(deadlineA, "a new attempt owns a fresh retry budget");
        scheduler.ScheduledTimeouts.Should().HaveCount(2);
        scheduler.ScheduledTimeouts[^1].Payload.Unpack<ActivateServiceRevisionCommand>()
            .ActivationAttemptId.Should().Be("attempt-b");
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleActivateAsync(callbackA);

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be("attempt-b");
        scheduler.ScheduledTimeouts.Should().HaveCount(2, "a stale callback must not re-arm itself");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitTerminalFailure_WhenRevisionPreparationFailed()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakePreparationFailedRevisionCatalogQueryReader(identity, "r1"),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();
        const string activationAttemptId = "attempt-preparation-failed";

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
        });

        agent.State.ActivationFailures.Should().ContainKey("r1");
        agent.State.ActivationFailures["r1"].FailureCode
            .Should().Be(ServiceDeploymentActivationFailureCode.RevisionPreparationFailed);
        agent.State.ActivationFailures["r1"].FailureReason.Should().Contain("failed preparation");
        agent.State.ActivationFailures["r1"].ActivationAttemptId.Should().Be(activationAttemptId);
        scheduler.ScheduledTimeouts.Should().ContainSingle(
            "the callback is installed before dependencies are inspected and becomes fenced after terminal failure");

        var committedVersion = agent.State.LastAppliedEventVersion;
        var firstFailureAt = agent.State.ActivationFailures["r1"].OccurredAt;
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
        });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion, "the same logical attempt is terminally idempotent");
        agent.State.ActivationFailures["r1"].OccurredAt.Should().Be(firstFailureAt);
    }

    [Fact]
    public async Task HandleActivateAsync_LegacyBlankAttemptAfterFailure_ShouldRemainFreshRetry()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakePreparationFailedRevisionCatalogQueryReader(identity, "r1"),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        var committedVersion = agent.State.LastAppliedEventVersion;
        var firstFailureAt = agent.State.ActivationFailures["r1"].OccurredAt;

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.LastAppliedEventVersion.Should().Be(
            committedVersion + 2,
            "each legacy fresh retry checkpoints pending before committing its terminal result");
        agent.State.ActivationFailures["r1"].ActivationAttemptId.Should().BeEmpty();
        agent.State.ActivationFailures["r1"].OccurredAt.ToDateTimeOffset().Should()
            .BeOnOrAfter(firstFailureAt.ToDateTimeOffset());
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldClearCommittedFailure_WhenRevisionLaterActivates()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-a",
        });
        var callbackA = ExpirePendingActivation(agent, scheduler, "r1");
        await agent.HandleActivateAsync(callbackA);
        agent.State.ActivationFailures.Should().ContainKey("r1");

        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-b",
        });

        agent.State.ActivationFailures.Should().NotContainKey("r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        activator.ActivationRequests.Should().ContainSingle();

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        await AcknowledgeLatestRemovalDispatchAsync(agent, identity, dispatchPort);
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleActivateAsync(callbackA);

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion, "a late callback must not revive an inactive revision");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
        agent.State.ActivationFailures["r1"].FailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.SupersededByDeactivation);
        activator.ActivationRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitSanitizedTerminalFailure_WhenAdmissionIsRejected()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            admissionEvaluator: new RejectActivationAdmissionEvaluator());
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-admission-rejected",
        });

        var failure = agent.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.FailureCode.Should().Be(ServiceDeploymentActivationFailureCode.AdmissionRejected);
        failure.FailureReason.Should().Be("Service activation admission was rejected.");
        failure.FailureReason.Should().NotContain("secret-policy-subject");
        failure.ActivationAttemptId.Should().Be("attempt-admission-rejected");
        agent.State.Deployments.Should().BeEmpty();
        activator.ActivationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitSanitizedTerminalFailure_WhenAdmissionEvaluationThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler,
            admissionEvaluator: new ThrowingActivationAdmissionEvaluator());
        await agent.ActivateAsync();

        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-admission-error",
        };
        await agent.HandleActivateAsync(command);

        agent.State.ActivationFailures.Should().BeEmpty();
        agent.State.PendingActivations.Should().ContainKey("r1");
        agent.State.PendingActivations["r1"].LastRetryFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed);
        agent.State.ToString().Should().NotContain("secret-admission-detail");
        scheduler.ScheduledTimeouts.Should().ContainSingle();

        var callback = ExpirePendingActivation(agent, scheduler, "r1");
        await agent.HandleActivateAsync(callback);

        var failure = agent.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.FailureCode.Should().Be(ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed);
        failure.FailureReason.Should().Be(
            "Service activation admission could not be evaluated before the activation deadline.");
        failure.FailureReason.Should().NotContain("secret-admission-detail");
        failure.ActivationAttemptId.Should().Be("attempt-admission-error");
        agent.State.Deployments.Should().BeEmpty();
        activator.ActivationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitSanitizedTerminalFailure_WhenRuntimeActivationThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator
        {
            ActivationException = new InvalidOperationException("secret-runtime-detail"),
        };
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-runtime-error",
        };
        await agent.HandleActivateAsync(command);

        agent.State.ActivationFailures.Should().BeEmpty();
        agent.State.PendingActivations.Should().ContainKey("r1");
        agent.State.PendingActivations["r1"].LastRetryFailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.RuntimeActivationFailed);
        agent.State.ToString().Should().NotContain("secret-runtime-detail");
        scheduler.ScheduledTimeouts.Should().ContainSingle();

        var callback = ExpirePendingActivation(agent, scheduler, "r1");
        await agent.HandleActivateAsync(callback);

        var failure = agent.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.FailureCode.Should().Be(ServiceDeploymentActivationFailureCode.RuntimeActivationFailed);
        failure.FailureReason.Should().Be(
            "Service runtime activation did not complete before the activation deadline.");
        failure.FailureReason.Should().NotContain("secret-runtime-detail");
        failure.ActivationAttemptId.Should().Be("attempt-runtime-error");
        agent.State.Deployments.Should().BeEmpty();
        activator.ActivationRequests.Should().ContainSingle(
            "an expired callback must use the checkpointed failure without calling runtime again");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRetryAdmissionEvaluationWithinActorOwnedDeadline()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var admissionEvaluator = new ThrowOnceActivationAdmissionEvaluator();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler: scheduler,
            admissionEvaluator: admissionEvaluator);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-admission-transient",
        });
        var callback = scheduler.ScheduledTimeouts.Should().ContainSingle().Subject.Payload
            .Unpack<ActivateServiceRevisionCommand>();

        await agent.HandleActivateAsync(callback);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        admissionEvaluator.RequestCount.Should().Be(2);
        activator.ActivationRequests.Should().ContainSingle();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRetryRuntimeActivationWithinActorOwnedDeadline()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationExceptions.Enqueue(
            new InvalidOperationException("secret-transient-runtime-detail"));
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-runtime-transient",
        });
        agent.State.ToString().Should().NotContain("secret-transient-runtime-detail");
        var callback = scheduler.ScheduledTimeouts.Should().ContainSingle().Subject.Payload
            .Unpack<ActivateServiceRevisionCommand>();

        await agent.HandleActivateAsync(callback);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);

        activator.ActivationRequests.Should().HaveCount(2);
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldCommitSanitizedFailure_WhenServingTargetDeliveryTimesOut()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var scheduler = new RecordingCallbackScheduler();
        var dispatchPort = new RecordingDispatchPort
        {
            DispatchException = new InvalidOperationException("secret-dispatch-detail"),
        };
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-delivery-timeout",
        });
        agent.State.ToString().Should().NotContain("secret-dispatch-detail");
        var callback = ExpirePendingActivation(agent, scheduler, "r1");

        await agent.HandleActivateAsync(callback);

        var failure = agent.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.FailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        failure.FailureReason.Should().Be(
            "Service serving target delivery did not complete before the activation deadline.");
        failure.FailureReason.Should().NotContain("secret-dispatch-detail");
        failure.ActivationAttemptId.Should().Be("attempt-delivery-timeout");
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        activator.ActivationRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldKeepPending_WhenServingTargetDispatchIsNotAdmitted()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort
        {
            DispatchAccepted = false,
        };
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-delivery-not-admitted",
        });

        dispatchPort.Commands.Should().ContainSingle();
        var pending = agent.State.PendingActivations.Should().ContainKey("r1").WhoseValue;
        pending.Phase.Should().Be(ServiceDeploymentActivationPhase.ServingTargetDispatchPending);
        pending.LastRetryFailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        agent.State.ActivationFailures.Should().BeEmpty();
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);

        var operationId = pending.ServingTargetOperationId;
        var commandId = pending.ServingTargetCommandId;
        dispatchPort.DispatchAccepted = true;
        var callback = scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>();
        await agent.HandleActivateAsync(callback);

        dispatchPort.Commands.Should().HaveCount(2);
        dispatchPort.Commands.Select(x => x.command.OperationId).Should().OnlyContain(x => x == operationId);
        dispatchPort.Envelopes.Select(x => x.Id).Should().OnlyContain(x => x == commandId);
        agent.State.PendingActivations["r1"].Phase.Should()
            .Be(ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        agent.State.PendingActivations.Should().NotContainKey("r1");
    }

    [Fact]
    public async Task HandleActivateAsync_NewAttemptShouldNotInheritPendingServingOperation()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var admission = new AllowActivationAdmissionEvaluator();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            admissionEvaluator: admission);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-a",
        });
        var firstCommand = dispatchPort.Commands.Single().command;
        var firstTarget = firstCommand.Targets.Single();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-b",
        });

        var secondCommand = dispatchPort.Commands[^1].command;
        secondCommand.ActivationAttemptId.Should().Be("attempt-b");
        secondCommand.OperationId.Should().NotBe(firstCommand.OperationId);
        dispatchPort.Envelopes[^1].Id.Should().NotBe(dispatchPort.Envelopes[0].Id);
        admission.RequestCount.Should().Be(2);
        activator.ActivationRequests.Should().ContainSingle();
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be("attempt-b");

        await agent.HandleServingTargetsAppliedAsync(
            CreateAppliedAck(identity, firstTarget, firstCommand));
        agent.State.PendingActivations.Should().ContainKey("r1");

        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationCompletions.Values.Should().ContainSingle(x =>
            x.ActivationAttemptId == "attempt-b" &&
            x.ServingTargetOperationId == secondCommand.OperationId);
    }

    [Fact]
    public async Task ActivateAsync_ShouldRestorePendingServingTargetDelivery_WithoutReactivatingRuntime()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            artifact);
        var firstActivator = new RecordingRuntimeActivator();
        firstActivator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var failedDispatch = new RecordingDispatchPort
        {
            DispatchException = new InvalidOperationException("secret-dispatch-detail"),
        };
        var firstScheduler = new RecordingCallbackScheduler();
        var actorId = ServiceActorIds.Deployment(identity);
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            firstActivator,
            actorId,
            failedDispatch,
            firstScheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-serving-delivery",
            ExpectedArtifactHash = artifact.ArtifactHash,
        });

        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        var pending = agent.State.PendingActivations.Should().ContainKey("r1").WhoseValue;
        pending.Phase.Should().Be(ServiceDeploymentActivationPhase.ServingTargetDispatchPending);
        pending.DeploymentId.Should().Be("dep-r1");
        pending.PrimaryActorId.Should().Be("actor-r1");
        pending.LastRetryFailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        pending.ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        agent.State.ActivationFailures.Should().BeEmpty();
        firstActivator.ActivationRequests.Should().ContainSingle();
        failedDispatch.Commands.Should().ContainSingle();
        var originalCommand = failedDispatch.Commands.Single().command;
        var originalEnvelopeId = failedDispatch.Envelopes.Single().Id;
        await agent.DeactivateAsync();

        var recoveredActivator = new RecordingRuntimeActivator();
        var recoveredDispatch = new RecordingDispatchPort();
        var recoveredScheduler = new RecordingCallbackScheduler();
        var replayed = CreateAgent(
            eventStore,
            revisionCatalog,
            recoveredActivator,
            actorId,
            recoveredDispatch,
            recoveredScheduler);

        await replayed.ActivateAsync();

        recoveredDispatch.Commands.Should().BeEmpty("activation recovery must enter through the actor inbox");
        var callback = recoveredScheduler.ScheduledTimeouts.Should().ContainSingle().Subject.Payload
            .Unpack<ActivateServiceRevisionCommand>();
        callback.ActivationAttemptId.Should().Be("attempt-serving-delivery");
        callback.ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        replayed.State.PendingActivations["r1"].ExpectedArtifactHash.Should().Be(artifact.ArtifactHash);
        await replayed.HandleActivateAsync(callback);

        recoveredActivator.ActivationRequests.Should().BeEmpty();
        recoveredDispatch.Commands.Should().ContainSingle();
        recoveredDispatch.Commands[0].command.OperationId.Should().Be(originalCommand.OperationId);
        recoveredDispatch.Commands[0].command.ActivationAttemptId.Should().Be(originalCommand.ActivationAttemptId);
        recoveredDispatch.Envelopes.Single().Id.Should().Be(originalEnvelopeId);
        recoveredDispatch.Commands[0].command.Targets.Should().ContainSingle();
        recoveredDispatch.Commands[0].command.Targets[0].DeploymentId.Should().Be("dep-r1");
        await AcknowledgeLatestServingDispatchAsync(replayed, identity, recoveredDispatch);
        replayed.State.PendingActivations.Should().NotContainKey("r1");
        replayed.State.ActivationFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyPendingServingDispatch_ShouldCheckpointPositiveSequenceBeforeRedispatch()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        BindActiveDeployment(agent, identity);
        agent.State.Deployments["dep-r1"].ArtifactHash = artifact.ArtifactHash;
        var command = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-legacy-sequence",
            ExpectedArtifactHash = artifact.ArtifactHash,
        };
        await agent.HandleActivateAsync(command);
        var legacy = agent.State.PendingActivations["r1"].Clone();
        legacy.Phase = ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted;
        legacy.ServingOperationSequence = 0;
        legacy.ServingTargetOperationId = string.Empty;
        legacy.ServingTargetCommandId = string.Empty;
        agent.State.PendingActivations["r1"] = legacy;
        dispatchPort.Commands.Clear();
        dispatchPort.Envelopes.Clear();

        await agent.HandleActivateAsync(command.Clone());

        var migrated = agent.State.PendingActivations["r1"];
        migrated.ServingOperationSequence.Should().BePositive();
        migrated.ServingTargetOperationId.Should().NotBeNullOrWhiteSpace();
        migrated.ServingTargetCommandId.Should().NotBeNullOrWhiteSpace();
        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].command.OperationSequence.Should()
            .Be(migrated.ServingOperationSequence);
        (await eventStore.GetEventsAsync(agent.Id)).Count(evt =>
                evt.EventData.Is(ServiceDeploymentServingTargetsDispatchPendingEvent.Descriptor))
            .Should().Be(2, "the legacy checkpoint must be replaced before redispatch");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReplayLegacyActivatedWireAndPreserveLegacyPendingClearSemantics()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Deployment(identity);
        var occurredAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1));
        await eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "legacy-deferred",
                    Version = 1,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(new ServiceDeploymentActivationDeferredEvent
                    {
                        Identity = identity.Clone(),
                        RevisionId = "r1",
                        DeadlineAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(4)),
                        DeferredAt = occurredAt.Clone(),
                        ActivationAttemptId = "legacy-attempt",
                    }),
                },
                new StateEvent
                {
                    EventId = "legacy-activated",
                    Version = 2,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(ParseLegacyActivatedWire(
                        identity,
                        "dep-r1",
                        "r1",
                        "actor-r1",
                        occurredAt)),
                },
            ],
            expectedVersion: 0);

        var replayed = CreateAgent(
            eventStore,
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            actorId);
        await replayed.ActivateAsync();

        replayed.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");
        replayed.State.PendingActivations.Should().NotContainKey("r1");
        replayed.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldPersistActivatedAndDispatchCheckpointAtTheSameCutPoint()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort
        {
            DispatchException = new InvalidOperationException("dispatch unavailable"),
        };
        var actorId = ServiceActorIds.Deployment(identity);
        var agent = CreateAgent(eventStore, revisionCatalog, activator, actorId, dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-atomic-cut",
        });

        var events = await eventStore.GetEventsAsync(actorId);
        var activatedIndex = events
            .Select((evt, index) => (evt, index))
            .Single(x => x.evt.EventData.Is(ServiceDeploymentActivatedEvent.Descriptor))
            .index;
        events[activatedIndex + 1].EventData
            .Is(ServiceDeploymentServingTargetsDispatchPendingEvent.Descriptor).Should().BeTrue();
        events[activatedIndex + 1].Version.Should().Be(events[activatedIndex].Version + 1);

        var replayed = CreateAgent(
            eventStore,
            revisionCatalog,
            new RecordingRuntimeActivator(),
            actorId);
        await replayed.ActivateAsync();
        replayed.State.Deployments.Should().ContainKey("dep-r1");
        replayed.State.PendingActivations.Should().ContainKey("r1");
        replayed.State.PendingActivations["r1"].DeploymentId.Should().Be("dep-r1");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldTreatDispatchAdmissionAsPendingUntilMatchingAppliedAck()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-ack",
        });

        var pending = agent.State.PendingActivations.Should().ContainKey("r1").WhoseValue;
        pending.Phase.Should().Be(ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted);
        agent.State.ActivationCompletions.Should().BeEmpty();
        var versionBeforeStaleAcks = agent.State.LastAppliedEventVersion;
        var command = dispatchPort.Commands.Single().command;
        var target = command.Targets.Single();
        foreach (var staleAck in new[]
                 {
                     CreateAppliedAck(identity, target, command, activationAttemptId: "stale-attempt"),
                     CreateAppliedAck(identity, target, command, operationId: "stale-operation"),
                     CreateAppliedAck(identity, target, command, deploymentId: "stale-deployment"),
                 })
        {
            await agent.HandleServingTargetsAppliedAsync(staleAck);
        }

        agent.State.LastAppliedEventVersion.Should().Be(versionBeforeStaleAcks);
        agent.State.PendingActivations.Should().ContainKey("r1");
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort, servingGeneration: 7);

        agent.State.PendingActivations.Should().NotContainKey("r1");
        var completion = agent.State.ActivationCompletions.Values.Should().ContainSingle().Subject;
        completion.ActivationAttemptId.Should().Be("attempt-ack");
        completion.DeploymentId.Should().Be("dep-r1");
        completion.ServingTargetOperationId.Should().Be(command.OperationId);
        completion.ServingGeneration.Should().Be(7);
    }

    [Fact]
    public async Task DefaultServingDispatchAdmission_ShouldRemainPendingAndRecoverStableCommandUntilCommitAck()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            scheduler);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-default-after-serving",
        });
        var servingCommand = dispatchPort.Commands.Single().command;
        var servingTarget = servingCommand.Targets.Single();

        await agent.HandleServingTargetsAppliedAsync(
            CreateAppliedAck(identity, servingTarget, servingCommand));

        dispatchPort.DefaultCommands.Should().ContainSingle();
        dispatchPort.DefaultCommands.Single().actorId.Should().Be(ServiceActorIds.Definition(identity));
        var defaultCommand = dispatchPort.DefaultCommands.Single().command;
        defaultCommand.RevisionId.Should().Be("r1");
        defaultCommand.OperationId.Should().Be(servingCommand.OperationId);
        defaultCommand.CommandId.Should().NotBeEmpty();
        defaultCommand.ReplyActorId.Should().Be(ServiceActorIds.Deployment(identity));
        defaultCommand.ActivationAttemptId.Should().Be("attempt-default-after-serving");
        defaultCommand.DeploymentId.Should().Be("dep-r1");
        var defaultEnvelopeId = dispatchPort.Envelopes
            .Single(x => x.Payload.Is(SetDefaultServingRevisionCommand.Descriptor))
            .Id;
        defaultEnvelopeId.Should().Be(defaultCommand.CommandId);
        agent.State.ActivationCompletions.Should().BeEmpty();
        var pending = agent.State.PendingActivations.Should().ContainKey("r1").WhoseValue;
        pending.Phase.Should().Be(
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending);
        pending.DefaultServingOperationId.Should().Be(defaultCommand.OperationId);
        pending.DefaultServingCommandId.Should().Be(defaultCommand.CommandId);
        pending.DefaultServingDispatchAcceptedAt.Should().NotBeNull();
        pending.ServingGeneration.Should().Be(defaultCommand.ServingGeneration);

        await agent.DeactivateAsync();

        var recoveredDispatch = new RecordingDispatchPort();
        var recoveredScheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(
            eventStore,
            revisionCatalog,
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity),
            recoveredDispatch,
            recoveredScheduler);
        await recovered.ActivateAsync();
        var callback = recoveredScheduler.ScheduledTimeouts.Single().Payload
            .Unpack<ActivateServiceRevisionCommand>();
        await recovered.HandleActivateAsync(callback);

        recoveredDispatch.Commands.Should().BeEmpty("recovery resumes the committed default-serving phase");
        var recoveredDefault = recoveredDispatch.DefaultCommands.Should().ContainSingle().Subject.command;
        recoveredDefault.OperationId.Should().Be(defaultCommand.OperationId);
        recoveredDefault.CommandId.Should().Be(defaultCommand.CommandId);
        recoveredDispatch.Envelopes.Single().Id.Should().Be(defaultEnvelopeId);
        recovered.State.ActivationCompletions.Should().BeEmpty(
            "dispatch admission is not definition commit");

        await AcknowledgeLatestDefaultServingRevisionAsync(recovered, identity, recoveredDispatch);

        recovered.State.PendingActivations.Should().NotContainKey("r1");
        var completion = recovered.State.ActivationCompletions.Values.Should().ContainSingle().Subject;
        completion.DefaultServingOperationId.Should().Be(defaultCommand.OperationId);
        completion.DefaultServingCommandId.Should().Be(defaultCommand.CommandId);
        completion.DefaultServingCommittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DefaultServingSupersededAck_ShouldFailPendingActivationImmediately()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-superseded",
        });
        var servingCommand = dispatchPort.Commands.Single().command;
        await agent.HandleServingTargetsAppliedAsync(
            CreateAppliedAck(identity, servingCommand.Targets.Single(), servingCommand));
        var defaultCommand = dispatchPort.DefaultCommands.Should().ContainSingle().Subject.command;
        var supersededAck = CreateDefaultServingCommittedAck(identity, defaultCommand);
        supersededAck.Disposition = DefaultServingRevisionCommitDisposition.Superseded;
        supersededAck.SupersededByGeneration = defaultCommand.ServingGeneration + 1;

        await agent.HandleDefaultServingRevisionCommittedAsync(supersededAck);

        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationCompletions.Should().BeEmpty();
        var failure = agent.State.ActivationFailures.Should().ContainKey("r1").WhoseValue;
        failure.ActivationAttemptId.Should().Be("attempt-superseded");
        failure.FailureCode.Should().Be(
            ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded);
        failure.FailureReason.Should().Be(
            "Default serving revision was superseded by a newer serving generation.");
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task HandleServingTargetsAppliedAsync_ShouldFenceAckAtActorObservedDeadline(
        int observedOffsetMilliseconds,
        bool expectedSuccess)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-15T00:00:00Z"));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            timeProvider: clock);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-ack-deadline",
        });
        var deadline = agent.State.PendingActivations["r1"].DeadlineAt.ToDateTime();
        var command = dispatchPort.Commands.Single().command;
        var ack = CreateAppliedAck(identity, command.Targets.Single(), command);
        ack.AppliedAt = Timestamp.FromDateTime(deadline.AddSeconds(-1));
        clock.SetUtcNow(new DateTimeOffset(deadline, TimeSpan.Zero)
            .AddMilliseconds(observedOffsetMilliseconds));

        await agent.HandleEventAsync(CreateAppliedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            ack));

        if (expectedSuccess)
        {
            var defaultCommand = dispatchPort.DefaultCommands.Should().ContainSingle().Subject.command;
            await agent.HandleEventAsync(CreateDefaultServingCommittedAckEnvelope(
                agent.Id,
                ServiceActorIds.Definition(identity),
                CreateDefaultServingCommittedAck(identity, defaultCommand)));
            agent.State.ActivationCompletions.Should().ContainSingle();
            agent.State.ActivationFailures.Should().BeEmpty();
        }
        else
        {
            agent.State.ActivationCompletions.Should().BeEmpty();
            agent.State.ActivationFailures["r1"].FailureCode.Should()
                .Be(ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        }
        agent.State.PendingActivations.Should().NotContainKey("r1");
    }

    [Fact]
    public async Task DeadlineAndCanonicalAckInboxOrder_ShouldConvergeToSameTerminalFailure()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();

        async Task<(ServiceDeploymentManagerGAgent Agent, FakeTimeProvider Clock,
            ActivateServiceRevisionCommand Callback, EventEnvelope AckEnvelope)> PrepareAsync()
        {
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-15T00:00:00Z"));
            var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
            await revisionCatalog.UpsertRevisionAsync(
                ServiceKeys.Build(identity),
                "r1",
                GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
            var activator = new RecordingRuntimeActivator();
            activator.ActivationResults.Enqueue(
                new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
            var dispatchPort = new RecordingDispatchPort();
            var scheduler = new RecordingCallbackScheduler();
            var agent = CreateAgent(
                new InMemoryEventStore(),
                revisionCatalog,
                activator,
                ServiceActorIds.Deployment(identity),
                dispatchPort,
                scheduler,
                timeProvider: clock);
            await agent.ActivateAsync();
            await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = "r1",
                ActivationAttemptId = "attempt-inbox-order",
            });
            var command = dispatchPort.Commands.Single().command;
            var ack = CreateAppliedAck(identity, command.Targets.Single(), command);
            ack.AppliedAt = Timestamp.FromDateTime(
                agent.State.PendingActivations["r1"].DeadlineAt.ToDateTime().AddSeconds(-1));
            return (
                agent,
                clock,
                scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>(),
                CreateAppliedAckEnvelope(
                    agent.Id,
                    ServiceActorIds.ServingSet(identity),
                    ack));
        }

        var ackFirst = await PrepareAsync();
        var timeoutFirst = await PrepareAsync();
        var deadline = ackFirst.Agent.State.PendingActivations["r1"].DeadlineAt.ToDateTime();
        ackFirst.Clock.SetUtcNow(new DateTimeOffset(deadline, TimeSpan.Zero));
        timeoutFirst.Clock.SetUtcNow(new DateTimeOffset(deadline, TimeSpan.Zero));

        await ackFirst.Agent.HandleEventAsync(ackFirst.AckEnvelope);
        await ackFirst.Agent.HandleActivateAsync(ackFirst.Callback);
        await timeoutFirst.Agent.HandleActivateAsync(timeoutFirst.Callback);
        await timeoutFirst.Agent.HandleEventAsync(timeoutFirst.AckEnvelope);

        ackFirst.Agent.State.ActivationCompletions.Should().BeEmpty();
        timeoutFirst.Agent.State.ActivationCompletions.Should().BeEmpty();
        ackFirst.Agent.State.ActivationFailures["r1"].FailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        timeoutFirst.Agent.State.ActivationFailures["r1"].FailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
        ackFirst.Agent.State.ToByteArray().Should().Equal(timeoutFirst.Agent.State.ToByteArray());
    }

    [Fact]
    public async Task ReplayActivationCheckpoints_WhenOptionalTimestampsAreMissing_ShouldBeByteDeterministic()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Deployment(identity);
        var epoch = Timestamp.FromDateTime(DateTime.UnixEpoch);
        var deadline = Timestamp.FromDateTime(new DateTime(2026, 8, 15, 0, 5, 0, DateTimeKind.Utc));
        var startedAt = Timestamp.FromDateTime(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc));
        var deferredAt = Timestamp.FromDateTime(new DateTime(2026, 8, 15, 0, 0, 1, DateTimeKind.Utc));
        var preparedAt = Timestamp.FromDateTime(new DateTime(2026, 8, 15, 0, 0, 2, DateTimeKind.Utc));

        async Task<ServiceDeploymentState> ReplayTwiceAsync(params IMessage[] domainEvents)
        {
            var eventStore = new InMemoryEventStore();
            var stateEvents = domainEvents.Select((domainEvent, index) => new StateEvent
            {
                AgentId = actorId,
                EventId = $"deterministic-replay-{index + 1}",
                EventType = domainEvent.Descriptor.FullName,
                EventData = Any.Pack(domainEvent),
                Timestamp = startedAt.Clone(),
                Version = index + 1,
            }).ToArray();
            await eventStore.AppendAsync(actorId, stateEvents, expectedVersion: 0);

            var first = CreateAgent(
                eventStore,
                new FakeServiceRevisionCatalogQueryReader(),
                new RecordingRuntimeActivator(),
                actorId);
            await first.ActivateAsync();
            var firstState = first.State.Clone();
            await first.DeactivateAsync();

            var second = CreateAgent(
                eventStore,
                new FakeServiceRevisionCatalogQueryReader(),
                new RecordingRuntimeActivator(),
                actorId);
            await second.ActivateAsync();
            second.State.ToByteArray().Should().Equal(firstState.ToByteArray());
            return firstState;
        }

        var deferredState = await ReplayTwiceAsync(new ServiceDeploymentActivationDeferredEvent
        {
            Identity = identity.Clone(),
            RevisionId = "r-deferred",
            ActivationAttemptId = "attempt-deferred",
        });
        var deferred = deferredState.PendingActivations["r-deferred"];
        deferred.DeadlineAt.Should().Be(epoch);
        deferred.DeferredAt.Should().Be(epoch);
        deferred.StartedAt.Should().Be(epoch);

        var invocationState = await ReplayTwiceAsync(
            new ServiceDeploymentActivationDeferredEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r-invocation",
                ActivationAttemptId = "attempt-invocation",
                DeadlineAt = deadline.Clone(),
                DeferredAt = deferredAt.Clone(),
                StartedAt = startedAt.Clone(),
            },
            new ServiceDeploymentRuntimeActivationInvocationStartedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r-invocation",
                ActivationAttemptId = "attempt-invocation",
                OperationId = "operation-invocation",
                InvocationCount = 1,
            });
        var invocation = invocationState.PendingActivations["r-invocation"];
        invocation.DeadlineAt.Should().Be(deadline);
        invocation.DeferredAt.Should().Be(deferredAt);
        invocation.StartedAt.Should().Be(startedAt);
        invocation.RuntimeActivationInvocationStartedAt.Should().BeNull();

        var pendingState = await ReplayTwiceAsync(
            new ServiceDeploymentServingTargetsDispatchPendingEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r-pending",
                DeploymentId = "deployment-pending",
                PrimaryActorId = "actor-pending",
                ActivationAttemptId = "attempt-pending",
                OperationId = "operation-pending",
                CommandId = "command-pending",
            });
        var pending = pendingState.PendingActivations["r-pending"];
        pending.DeadlineAt.Should().Be(epoch);
        pending.DeferredAt.Should().Be(epoch);
        pending.StartedAt.Should().Be(epoch);

        var dispatchPending = new ServiceDeploymentServingTargetsDispatchPendingEvent
        {
            Identity = identity.Clone(),
            RevisionId = "r-applied",
            DeploymentId = "deployment-applied",
            PrimaryActorId = "actor-applied",
            ActivationAttemptId = "attempt-applied",
            OperationId = "operation-applied",
            CommandId = "command-applied",
            DeadlineAt = deadline.Clone(),
            ActivationStartedAt = startedAt.Clone(),
            PreparedAt = preparedAt.Clone(),
        };
        var dispatchAccepted = new ServiceDeploymentServingTargetsDispatchAcceptedEvent
        {
            Identity = identity.Clone(),
            RevisionId = "r-applied",
            DeploymentId = "deployment-applied",
            ActivationAttemptId = "attempt-applied",
            OperationId = "operation-applied",
            CommandId = "command-applied",
        };
        var acceptedState = await ReplayTwiceAsync(dispatchPending, dispatchAccepted);
        acceptedState.PendingActivations["r-applied"].ServingTargetDispatchAcceptedAt
            .Should().Be(preparedAt);

        var appliedState = await ReplayTwiceAsync(
            dispatchPending,
            dispatchAccepted,
            new ServiceDeploymentServingTargetsAppliedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r-applied",
                DeploymentId = "deployment-applied",
                ActivationAttemptId = "attempt-applied",
                OperationId = "operation-applied",
                ServingGeneration = 7,
            });
        appliedState.PendingActivations.Should().NotContainKey("r-applied");
        appliedState.ActivationCompletions.Values.Should().ContainSingle()
            .Which.CompletedAt.Should().Be(preparedAt);
    }

    [Fact]
    public async Task HandleServingTargetsAppliedEnvelope_ShouldIgnoreForeignPublisher()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-foreign-ack",
        });
        var command = dispatchPort.Commands.Single().command;
        var ack = CreateAppliedAck(identity, command.Targets.Single(), command);
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleEventAsync(CreateAppliedAckEnvelope(agent.Id, "foreign-serving-set", ack));

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.PendingActivations.Should().ContainKey("r1");
        agent.State.ActivationCompletions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleServingTargetsAppliedEnvelope_ShouldAcceptCanonicalServingSetPublisher()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-canonical-ack",
        });
        var command = dispatchPort.Commands.Single().command;
        var ack = CreateAppliedAck(identity, command.Targets.Single(), command);

        await agent.HandleEventAsync(CreateAppliedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            ack));

        agent.State.PendingActivations["r1"].Phase.Should().Be(
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending);
        var defaultCommand = dispatchPort.DefaultCommands.Should().ContainSingle().Subject.command;
        var defaultAck = CreateDefaultServingCommittedAck(identity, defaultCommand);
        var defaultPendingVersion = agent.State.LastAppliedEventVersion;
        foreach (var staleAck in new[]
                 {
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         activationAttemptId: "stale-attempt"),
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         operationId: "stale-operation"),
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         commandId: "stale-command"),
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         deploymentId: "stale-deployment"),
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         servingGeneration: defaultCommand.ServingGeneration + 1),
                     CreateDefaultServingCommittedAck(
                         identity,
                         defaultCommand,
                         disposition: DefaultServingRevisionCommitDisposition.Unspecified),
                 })
        {
            await agent.HandleDefaultServingRevisionCommittedAsync(staleAck);
        }
        await agent.HandleEventAsync(CreateDefaultServingCommittedAckEnvelope(
            agent.Id,
            "foreign-definition",
            defaultAck));
        agent.State.LastAppliedEventVersion.Should().Be(defaultPendingVersion);

        await agent.HandleEventAsync(CreateDefaultServingCommittedAckEnvelope(
            agent.Id,
            ServiceActorIds.Definition(identity),
            defaultAck));

        agent.State.PendingActivations.Should().NotContainKey("r1");
        var completion = agent.State.ActivationCompletions.Values.Should().ContainSingle().Subject;
        completion.ActivationAttemptId.Should().Be("attempt-canonical-ack");
        completion.ServingTargetOperationId.Should().Be(command.OperationId);
        var completedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleEventAsync(CreateDefaultServingCommittedAckEnvelope(
            agent.Id,
            ServiceActorIds.Definition(identity),
            defaultAck));

        agent.State.LastAppliedEventVersion.Should().Be(completedVersion);
        agent.State.ActivationCompletions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldNoOpExactCompletedAttemptButReadmitDifferentAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(
            new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var admission = new AllowActivationAdmissionEvaluator();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            dispatchPort,
            admissionEvaluator: admission);
        await agent.ActivateAsync();
        var completedCommand = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-completed",
        };

        await agent.HandleActivateAsync(completedCommand);
        await AcknowledgeLatestServingDispatchAsync(agent, identity, dispatchPort);
        var completedVersion = agent.State.LastAppliedEventVersion;
        var admissionCount = admission.RequestCount;
        var runtimeCount = activator.ActivationRequests.Count;
        var dispatchCount = dispatchPort.Commands.Count;

        await agent.HandleActivateAsync(completedCommand.Clone());

        agent.State.LastAppliedEventVersion.Should().Be(completedVersion);
        admission.RequestCount.Should().Be(admissionCount);
        activator.ActivationRequests.Should().HaveCount(runtimeCount);
        dispatchPort.Commands.Should().HaveCount(dispatchCount);

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-different",
        });

        admission.RequestCount.Should().Be(admissionCount + 1);
        activator.ActivationRequests.Should().HaveCount(runtimeCount);
        dispatchPort.Commands.Should().HaveCount(dispatchCount + 1);
        agent.State.PendingActivations["r1"].ActivationAttemptId.Should().Be("attempt-different");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldReuseRuntimeOperationAfterSideEffectThenThrow()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new SideEffectThenThrowRuntimeActivator();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity),
            scheduler: scheduler);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = "attempt-runtime-operation",
        });
        var callback = scheduler.ScheduledTimeouts.Single().Payload.Unpack<ActivateServiceRevisionCommand>();
        await agent.HandleActivateAsync(callback);

        activator.ActivationRequests.Should().HaveCount(2);
        var operationIds = activator.ActivationRequests.Select(x => x.ActivationOperationId).ToArray();
        operationIds.Should().OnlyContain(x => x == operationIds[0]);
        operationIds.Should().NotContain(string.Empty);
        activator.RuntimeSideEffectCount.Should().Be(1);
        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.PendingActivations["r1"].Phase.Should()
            .Be(ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldIgnoreLateRuntimeSuccessAfterActorOwnedDeadline()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var lateResult = new TaskCompletionSource<ServiceRuntimeActivationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateObservationLogged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellation = new ManualResetEventSlim(false);
        var invocationCount = 0;
        var activator = new RecordingRuntimeActivator
        {
            ActivationAsyncOverride = (_, cancellationToken) =>
            {
                if (++invocationCount == 1)
                {
                    return Task.FromException<ServiceRuntimeActivationResult>(
                        new InvalidOperationException("first runtime call failed"));
                }

                cancellationToken.Register(() =>
                {
                    cancellationObserved.TrySetResult();
                    releaseCancellation.Wait(TimeSpan.FromSeconds(5));
                });
                return lateResult.Task;
            },
        };
        var agent = CreateAgent(
            new InMemoryEventStore(),
            revisionCatalog,
            activator,
            ServiceActorIds.Deployment(identity));
        agent.Logger = new SignalingLogger(
            "Late external dependency task completed after its deadline.",
            lateObservationLogged);
        await agent.ActivateAsync();
        const string activationAttemptId = "attempt-late-runtime";

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
        });
        var deadline = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(1));
        agent.State.PendingActivations["r1"].DeadlineAt = deadline.Clone();
        var continuation = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            ActivationAttemptId = activationAttemptId,
            ActivationDeadlineAt = deadline.Clone(),
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await agent.HandleActivateAsync(continuation).WaitAsync(TimeSpan.FromSeconds(2));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCancellation.Set();
        }
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        agent.State.PendingActivations.Should().NotContainKey("r1");
        agent.State.ActivationFailures["r1"].FailureCode.Should()
            .Be(ServiceDeploymentActivationFailureCode.RuntimeActivationFailed);
        agent.State.Deployments.Should().BeEmpty();
        var terminalVersion = agent.State.LastAppliedEventVersion;

        lateResult.SetResult(new ServiceRuntimeActivationResult("dep-late", "actor-late", "active"));
        await lateObservationLogged.Task.WaitAsync(TimeSpan.FromSeconds(2));

        agent.State.LastAppliedEventVersion.Should().Be(terminalVersion);
        agent.State.Deployments.Should().BeEmpty();
    }

    [Fact]
    public void GetRequiredPreparedArtifact_ShouldThrow_WhenRevisionMissing()
    {
        // Non-activation callers still require a materialized prepared artifact immediately.
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalog = new ServiceRevisionCatalogSnapshot(
            ServiceKeys.Build(identity),
            Revisions: [],
            UpdatedAt: DateTimeOffset.UtcNow);

        var act = () => catalog.GetRequiredPreparedArtifact(identity, "missing");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Prepared artifact*was not found*");
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldDeactivateSpecificActiveDeployment()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity), dispatchPort);

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        await AcknowledgeLatestRemovalDispatchAsync(agent, identity, dispatchPort);

        activator.DeactivateRequests.Should().ContainSingle(x => x.DeploymentId == "dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldIgnoreUnknownOrInactiveDeployment_WhenStateAlreadyExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity), dispatchPort);

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "missing",
        });
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });
        await AcknowledgeLatestRemovalDispatchAsync(agent, identity, dispatchPort);
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
        });

        activator.DeactivateRequests.Should().ContainSingle(x => x.DeploymentId == "dep-r1");
        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldRejectUnknownIdentity_WhenStateHasNotBeenInitialized()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));

        var act = () => agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "missing",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectBlankRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new FakeServiceRevisionCatalogQueryReader(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));

        var act = () => agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = " ",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision_id is required*");
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldRejectMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity(serviceId: "svc-other");
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity));
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var act = () => agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = otherIdentity.Clone(),
            DeploymentId = "dep-r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    private static ServiceRevisionSnapshot CreateRevisionSnapshot(
        PreparedServiceRevisionArtifact artifact,
        ServiceRevisionStatus status,
        string snapshotArtifactHash) =>
        new(
            artifact.RevisionId,
            artifact.ImplementationKind.ToString(),
            status.ToString(),
            snapshotArtifactHash,
            string.Empty,
            [],
            DateTimeOffset.UtcNow,
            status is ServiceRevisionStatus.Prepared or ServiceRevisionStatus.Published
                ? DateTimeOffset.UtcNow
                : null,
            status == ServiceRevisionStatus.Published ? DateTimeOffset.UtcNow : null,
            null,
            PreparedArtifact: artifact.Clone());

    private static ActivateServiceRevisionCommand ExpirePendingActivation(
        ServiceDeploymentManagerGAgent agent,
        RecordingCallbackScheduler scheduler,
        string revisionId)
    {
        var callback = scheduler.ScheduledTimeouts[^1].Payload.Unpack<ActivateServiceRevisionCommand>();
        var expiredDeadline = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1));
        agent.State.PendingActivations[revisionId].DeadlineAt = expiredDeadline.Clone();
        callback.ActivationDeadlineAt = expiredDeadline;
        return callback;
    }

    private static ServiceDeploymentActivatedEvent ParseLegacyActivatedWire(
        ServiceIdentity identity,
        string deploymentId,
        string revisionId,
        string primaryActorId,
        Timestamp activatedAt)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteMessage(identity);
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(deploymentId);
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteString(revisionId);
            output.WriteTag(4, WireFormat.WireType.LengthDelimited);
            output.WriteString(primaryActorId);
            output.WriteTag(5, WireFormat.WireType.Varint);
            output.WriteEnum((int)ServiceDeploymentStatus.Active);
            output.WriteTag(6, WireFormat.WireType.LengthDelimited);
            output.WriteMessage(activatedAt);
            output.Flush();
        }

        return ServiceDeploymentActivatedEvent.Parser.ParseFrom(stream.ToArray());
    }

    private static ServiceServingTargetsAppliedAck CreateAppliedAck(
        ServiceIdentity identity,
        ServiceServingTargetSpec target,
        ReplaceResolvedServiceServingTargetsCommand command,
        string? activationAttemptId = null,
        string? operationId = null,
        string? deploymentId = null) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = target.RevisionId,
            DeploymentId = deploymentId ?? target.DeploymentId,
            ActivationAttemptId = activationAttemptId ?? command.ActivationAttemptId,
            OperationId = operationId ?? command.OperationId,
            ServingGeneration = 1,
            AppliedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            OperationSequence = command.OperationSequence,
            Disposition = ServiceServingTargetsApplyDisposition.Applied,
        };

    private static EventEnvelope CreateAppliedAckEnvelope(
        string subscriberActorId,
        string publisherActorId,
        ServiceServingTargetsAppliedAck ack) =>
        new()
        {
            Id = $"serving-applied:{ack.OperationId}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(ack),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, subscriberActorId),
            Propagation = new EnvelopePropagation(),
        };

    private static async Task AcknowledgeLatestServingDispatchAsync(
        ServiceDeploymentManagerGAgent agent,
        ServiceIdentity identity,
        RecordingDispatchPort dispatchPort,
        long servingGeneration = 1)
    {
        var command = dispatchPort.Commands[^1].command;
        var target = command.Targets.Single();
        await agent.HandleServingTargetsAppliedAsync(new ServiceServingTargetsAppliedAck
        {
            Identity = identity.Clone(),
            RevisionId = target.RevisionId,
            DeploymentId = target.DeploymentId,
            ActivationAttemptId = command.ActivationAttemptId,
            OperationId = command.OperationId,
            ServingGeneration = servingGeneration,
            AppliedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            OperationSequence = command.OperationSequence,
            Disposition = ServiceServingTargetsApplyDisposition.Applied,
        });
        await AcknowledgeLatestDefaultServingRevisionAsync(agent, identity, dispatchPort);
    }

    private static Task AcknowledgeLatestDefaultServingRevisionAsync(
        ServiceDeploymentManagerGAgent agent,
        ServiceIdentity identity,
        RecordingDispatchPort dispatchPort)
    {
        var command = dispatchPort.DefaultCommands[^1].command;
        return agent.HandleDefaultServingRevisionCommittedAsync(
            CreateDefaultServingCommittedAck(identity, command));
    }

    private static async Task AcknowledgeLatestRemovalDispatchAsync(
        ServiceDeploymentManagerGAgent agent,
        ServiceIdentity identity,
        RecordingDispatchPort dispatchPort)
    {
        var command = dispatchPort.RemovalCommands[^1].command;
        await agent.HandleEventAsync(CreateRemovedAckEnvelope(
            agent.Id,
            ServiceActorIds.ServingSet(identity),
            new ServiceServingTargetsRemovedAck
            {
                Identity = identity.Clone(),
                DeploymentId = command.DeploymentId,
                RevisionId = command.RevisionId,
                PrimaryActorId = command.PrimaryActorId,
                ActivationAttemptId = command.ActivationAttemptId,
                ServingTargetOperationId = command.ServingTargetOperationId,
                DeactivationOperationId = command.DeactivationOperationId,
                Disposition = ServiceServingTargetRemovalDisposition.Removed,
                RemovedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }));
    }

    private static DefaultServingRevisionCommittedAck CreateDefaultServingCommittedAck(
        ServiceIdentity identity,
        SetDefaultServingRevisionCommand command,
        string? activationAttemptId = null,
        string? operationId = null,
        string? commandId = null,
        string? deploymentId = null,
        long? servingGeneration = null,
        DefaultServingRevisionCommitDisposition disposition =
            DefaultServingRevisionCommitDisposition.Applied) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = command.RevisionId,
            ActivationAttemptId = activationAttemptId ?? command.ActivationAttemptId,
            OperationId = operationId ?? command.OperationId,
            CommandId = commandId ?? command.CommandId,
            DeploymentId = deploymentId ?? command.DeploymentId,
            ServingGeneration = servingGeneration ?? command.ServingGeneration,
            CommittedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Disposition = disposition,
        };

    private static EventEnvelope CreateDefaultServingCommittedAckEnvelope(
        string subscriberActorId,
        string publisherActorId,
        DefaultServingRevisionCommittedAck ack) =>
        new()
        {
            Id = $"default-serving-committed:{ack.OperationId}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(ack),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, subscriberActorId),
            Propagation = new EnvelopePropagation(),
        };

    private static void BindActiveDeployment(ServiceDeploymentManagerGAgent agent, ServiceIdentity identity)
    {
        agent.State.Identity = identity.Clone();
        agent.State.Deployments["dep-r1"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-r1",
            RevisionId = "r1",
            PrimaryActorId = "actor-r1",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            ActivationAttemptId = "attempt-r1",
            ServingTargetOperationId = "operation-r1",
        };
    }

    private static ServiceServingTargetsRemovedAck CreateRemovedAck(
        ServiceIdentity identity,
        string operationId = "operation-r1",
        string deactivationOperationId = "deactivation-r1") =>
        new()
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-r1",
            RevisionId = "r1",
            PrimaryActorId = "actor-r1",
            ActivationAttemptId = "attempt-r1",
            ServingTargetOperationId = operationId,
            DeactivationOperationId = deactivationOperationId,
            Disposition = ServiceServingTargetRemovalDisposition.Removed,
            RemovedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static EventEnvelope CreateRemovedAckEnvelope(
        string subscriberActorId,
        string publisherActorId,
        ServiceServingTargetsRemovedAck ack) =>
        new()
        {
            Id = $"serving-removed:{ack.ServingTargetOperationId}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(ack),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, subscriberActorId),
            Propagation = new EnvelopePropagation(),
        };

    private static ServiceDeploymentManagerGAgent CreateAgent(
        InMemoryEventStore eventStore,
        IServiceRevisionCatalogQueryReader revisionCatalog,
        IServiceRuntimeActivator activator,
        string actorId,
        RecordingDispatchPort? dispatchPort = null,
        RecordingCallbackScheduler? scheduler = null,
        IActivationCapabilityViewReader? capabilityViewReader = null,
        IActivationAdmissionEvaluator? admissionEvaluator = null,
        TimeProvider? timeProvider = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceDeploymentManagerGAgent, ServiceDeploymentState>(
            eventStore,
            actorId,
            () => new ServiceDeploymentManagerGAgent(
                dispatchPort ?? new RecordingDispatchPort(),
                revisionCatalog,
                capabilityViewReader ?? new AlwaysReadyCapabilityViewReader(),
                admissionEvaluator ?? new AllowActivationAdmissionEvaluator(),
                activator,
                timeProvider),
            scheduler == null
                ? null
                : services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public Exception? DispatchException { get; set; }

        public bool DispatchAccepted { get; set; } = true;

        public List<(string actorId, ReplaceResolvedServiceServingTargetsCommand command)> Commands { get; } = [];

        public List<(string actorId, SetDefaultServingRevisionCommand command)> DefaultCommands { get; } = [];

        public List<(string actorId, RemoveDeploymentFromServiceServingTargetsCommand command)> RemovalCommands { get; } = [];

        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add(envelope.Clone());
            if (envelope.Payload.Is(ReplaceResolvedServiceServingTargetsCommand.Descriptor))
                Commands.Add((actorId, envelope.Payload.Unpack<ReplaceResolvedServiceServingTargetsCommand>()));
            else if (envelope.Payload.Is(SetDefaultServingRevisionCommand.Descriptor))
                DefaultCommands.Add((actorId, envelope.Payload.Unpack<SetDefaultServingRevisionCommand>()));
            else if (envelope.Payload.Is(RemoveDeploymentFromServiceServingTargetsCommand.Descriptor))
                RemovalCommands.Add((actorId, envelope.Payload.Unpack<RemoveDeploymentFromServiceServingTargetsCommand>()));
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope) with
            {
                Accepted = DispatchAccepted,
            });
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

    private sealed class DeferredCapabilityViewReader : IActivationCapabilityViewReader
    {
        public bool IsReady { get; set; }

        public int RequestCount { get; private set; }

        public Task<ActivationCapabilityView> GetAsync(
            ServiceIdentity identity,
            string revisionId,
            CancellationToken ct = default)
        {
            RequestCount++;
            if (!IsReady)
            {
                throw new ActivationCapabilityViewNotReadyException(
                    ServiceKeys.Build(identity),
                    revisionId,
                    ActivationCapabilityViewProjection.ServiceCatalog);
            }

            return Task.FromResult(new ActivationCapabilityView
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
            });
        }
    }

    private sealed class AllowActivationAdmissionEvaluator : IActivationAdmissionEvaluator
    {
        public int RequestCount { get; private set; }

        public Task<ActivationAdmissionDecision> EvaluateAsync(
            ActivationAdmissionRequest request,
            CancellationToken ct = default)
        {
            RequestCount++;
            return Task.FromResult(new ActivationAdmissionDecision
            {
                Allowed = true,
            });
        }
    }

    private sealed class RejectActivationAdmissionEvaluator : IActivationAdmissionEvaluator
    {
        public int RequestCount { get; private set; }

        public Task<ActivationAdmissionDecision> EvaluateAsync(
            ActivationAdmissionRequest request,
            CancellationToken ct = default)
        {
            RequestCount++;
            return Task.FromResult(new ActivationAdmissionDecision
            {
                Allowed = false,
                Violations =
                {
                    new AdmissionViolation
                    {
                        Code = "missing_binding",
                        SubjectId = "secret-policy-subject",
                        Message = "secret-policy-message",
                    },
                },
            });
        }
    }

    private sealed class ThrowingActivationAdmissionEvaluator : IActivationAdmissionEvaluator
    {
        public Task<ActivationAdmissionDecision> EvaluateAsync(
            ActivationAdmissionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("secret-admission-detail");
    }

    private sealed class ThrowOnceActivationAdmissionEvaluator : IActivationAdmissionEvaluator
    {
        public int RequestCount { get; private set; }

        public Task<ActivationAdmissionDecision> EvaluateAsync(
            ActivationAdmissionRequest request,
            CancellationToken ct = default)
        {
            RequestCount++;
            if (RequestCount == 1)
                throw new InvalidOperationException("secret-transient-admission-detail");

            return Task.FromResult(new ActivationAdmissionDecision
            {
                Allowed = true,
            });
        }
    }

    private sealed class RecordingRuntimeActivator : IServiceRuntimeActivator
    {
        public Exception? ActivationException { get; init; }

        public Queue<Exception> ActivationExceptions { get; } = [];

        public Queue<ServiceRuntimeActivationResult> ActivationResults { get; } = new();

        public Func<ServiceRuntimeActivationRequest, CancellationToken, Task<ServiceRuntimeActivationResult>>?
            ActivationAsyncOverride { get; init; }

        public List<ServiceRuntimeActivationRequest> ActivationRequests { get; } = [];

        public List<ServiceRuntimeDeactivationRequest> DeactivateRequests { get; } = [];

        public Queue<Exception> DeactivationExceptions { get; } = [];

        public Task<ServiceRuntimeActivationResult> ActivateAsync(
            ServiceRuntimeActivationRequest request,
            CancellationToken ct = default)
        {
            ActivationRequests.Add(request);
            if (ActivationAsyncOverride != null)
                return ActivationAsyncOverride(request, ct);
            if (ActivationExceptions.TryDequeue(out var activationException))
                throw activationException;
            if (ActivationException != null)
                throw ActivationException;
            if (ActivationResults.Count == 0)
                throw new InvalidOperationException("No activation result configured.");

            return Task.FromResult(ActivationResults.Dequeue());
        }

        public Task DeactivateAsync(ServiceRuntimeDeactivationRequest request, CancellationToken ct = default)
        {
            DeactivateRequests.Add(request);
            if (DeactivationExceptions.TryDequeue(out var deactivationException))
                throw deactivationException;
            return Task.CompletedTask;
        }
    }

    private sealed class SideEffectThenThrowRuntimeActivator : IServiceRuntimeActivator
    {
        private readonly Dictionary<string, ServiceRuntimeActivationResult> _results = new(StringComparer.Ordinal);
        private bool _firstInvocation = true;

        public int RuntimeSideEffectCount { get; private set; }

        public List<ServiceRuntimeActivationRequest> ActivationRequests { get; } = [];

        public Task<ServiceRuntimeActivationResult> ActivateAsync(
            ServiceRuntimeActivationRequest request,
            CancellationToken ct = default)
        {
            ActivationRequests.Add(request);
            if (!_results.TryGetValue(request.ActivationOperationId, out var result))
            {
                RuntimeSideEffectCount++;
                result = new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active");
                _results[request.ActivationOperationId] = result;
            }

            if (_firstInvocation)
            {
                _firstInvocation = false;
                throw new InvalidOperationException("runtime failed after applying its side effect");
            }

            return Task.FromResult(result);
        }

        public Task DeactivateAsync(ServiceRuntimeDeactivationRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<EventEnvelope> ScheduledTimeouts { get; } = [];

        public Queue<Exception> ScheduleTimeoutExceptions { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            if (ScheduleTimeoutExceptions.TryDequeue(out var exception))
                throw exception;

            ScheduledTimeouts.Add(request.TriggerEnvelope.Clone());
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ScheduledTimeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SignalingLogger(string expectedMessage, TaskCompletionSource signal) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(expectedMessage, StringComparison.Ordinal))
                signal.TrySetResult();
        }
    }

    private sealed class FakePreparationFailedRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        private readonly ServiceIdentity _identity;
        private readonly string _revisionId;

        public FakePreparationFailedRevisionCatalogQueryReader(ServiceIdentity identity, string revisionId)
        {
            _identity = identity;
            _revisionId = revisionId;
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var revision = new ServiceRevisionSnapshot(
                _revisionId,
                ServiceImplementationKind.Static.ToString(),
                ServiceRevisionStatus.PreparationFailed.ToString(),
                ArtifactHash: string.Empty,
                FailureReason: "boom",
                Endpoints: [],
                CreatedAt: DateTimeOffset.UtcNow,
                PreparedAt: null,
                PublishedAt: null,
                RetiredAt: null);
            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                ServiceKeys.Build(_identity),
                [revision],
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class SnapshotRevisionCatalogQueryReader(ServiceRevisionSnapshot revision)
        : IServiceRevisionCatalogQueryReader
    {
        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                ServiceKeys.Build(identity),
                [revision],
                DateTimeOffset.UtcNow));
    }
}
