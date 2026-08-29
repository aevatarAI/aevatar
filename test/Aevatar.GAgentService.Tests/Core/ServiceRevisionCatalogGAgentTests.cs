using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
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
        createObservation.Revisions.Should().BeEmpty();
        createObservation.RevisionReadiness.Should().ContainKey("r1")
            .WhoseValue.Status.Should().Be(ServiceRevisionStatus.Created);
        var prepareObservation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        prepareObservation.SourceRevisionVersion.Should().Be(2);
        prepareObservation.RevisionReadiness["r1"].Status.Should().Be(ServiceRevisionStatus.Prepared);
        prepareObservation.RevisionReadiness["r1"].PreparedEndpointIds.Should().Equal("run");
        var publishObservation = dispatchPort.Calls[2].Envelope.Payload.Unpack<ObserveServiceInvocationRevisionsCommand>();
        publishObservation.SourceRevisionVersion.Should().Be(3);
        publishObservation.Identity.Should().BeEquivalentTo(identity);
        publishObservation.RevisionReadiness["r1"].Status.Should().Be(ServiceRevisionStatus.Published);

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();
        replayed.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Published);
    }

    [Fact]
    public async Task RefreshInvocationCatalogObservation_ShouldRedispatchCommittedRevisionsWithoutMutatingState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-refresh-revisions");
        const string revisionId = "rev-refresh";
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(_ => Task.FromResult(
                GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId))),
            ServiceActorIds.RevisionCatalog(identity),
            dispatchPort);
        await agent.ActivateAsync();
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
        var committedVersion = agent.State.LastAppliedEventVersion;
        dispatchPort.Calls.Clear();

        await agent.HandleRefreshInvocationCatalogObservationAsync(
            new RefreshServiceInvocationCatalogObservationCommand
            {
                Identity = identity.Clone(),
            });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].ActorId.Should().Be(ServiceActorIds.InvocationCatalog(identity));
        var observation = dispatchPort.Calls[0].Envelope.Payload
            .Unpack<ObserveServiceInvocationRevisionsCommand>();
        observation.Identity.Should().BeEquivalentTo(identity);
        observation.SourceRevisionVersion.Should().Be(committedVersion);
        observation.Revisions.Should().BeEmpty();
        observation.RevisionReadiness.Should().ContainKey(revisionId)
            .WhoseValue.Status.Should().Be(ServiceRevisionStatus.Published);
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
        replayObservation.RevisionReadiness[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
    }

    [Theory]
    [InlineData("record-hash")]
    [InlineData("artifact-content")]
    public async Task PreparePreparedRevision_ShouldRejectInconsistentReusableArtifact(string corruption)
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-prepared-artifact-integrity");
        const string revisionId = "rev-prepared-artifact-integrity";
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId)));
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
        var record = agent.State.Revisions[revisionId];
        if (corruption == "record-hash")
            record.ArtifactHash = new string('A', 64);
        else
            record.PreparedArtifact.Endpoints[0].Description = "tampered after prepare";
        var committedVersion = agent.State.LastAppliedEventVersion;
        var observationCount = dispatchPort.Calls.Count;

        var replay = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent*");
        adapter.PrepareCalls.Should().Be(1);
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        dispatchPort.Calls.Should().HaveCount(observationCount);
    }

    [Fact]
    public async Task PublishPublishedRevision_ShouldRejectSelfInvalidReusableArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-published-artifact-integrity");
        const string revisionId = "rev-published-artifact-integrity";
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId)));
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
        agent.State.Revisions[revisionId].PreparedArtifact.Endpoints[0].Description =
            "tampered after publish";
        var committedVersion = agent.State.LastAppliedEventVersion;
        var observationCount = dispatchPort.Calls.Count;

        var replay = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent*");
        adapter.PrepareCalls.Should().Be(2);
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        dispatchPort.Calls.Should().HaveCount(observationCount);
    }

    [Fact]
    public async Task PublishPreparedRevision_ShouldRejectInconsistentPersistedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-prepared-publish-integrity");
        const string revisionId = "rev-prepared-publish-integrity";
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId)));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        agent.State.Revisions[revisionId].PreparedArtifact.Endpoints[0].Description =
            "tampered before publish";

        var publish = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await publish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent*");
        adapter.PrepareCalls.Should().Be(1);
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Prepared);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("implementation-kind")]
    [InlineData("deployment-plan-kind")]
    public async Task HandlePrepareRevisionAsync_ShouldRejectAdapterArtifactNotBoundToSpec(
        string mismatch)
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-adapter-target-binding");
        const string revisionId = "rev-adapter-target-binding";
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, revisionId);
        switch (mismatch)
        {
            case "identity":
                artifact.Identity = GAgentServiceTestKit.CreateIdentity("svc-other");
                break;
            case "revision":
                artifact.RevisionId = "rev-other";
                break;
            case "implementation-kind":
                artifact.ImplementationKind = ServiceImplementationKind.Scripting;
                break;
            case "deployment-plan-kind":
                artifact.DeploymentPlan = new ServiceDeploymentPlan
                {
                    ScriptingPlan = new ScriptingServiceDeploymentPlan
                    {
                        ScriptId = "script-other",
                    },
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
        var adapter = new RecordingAdapter(_ => Task.FromResult(artifact.Clone()));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
        });

        var prepare = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent with its authoring spec*");
        adapter.PrepareCalls.Should().Be(1);
        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.PreparationFailed);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldRejectWorkflowArtifactForDifferentDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-workflow-definition-binding");
        const string revisionId = "rev-workflow-definition-binding";
        var spec = CreateWorkflowRevisionSpec(identity, revisionId);
        var artifact = CreateWorkflowArtifact(
            identity,
            revisionId,
            ExternalCapabilityExecutionMode.Interactive);
        artifact.DeploymentPlan.WorkflowPlan.WorkflowYaml = "name: different-workflow\nsteps: []";
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(artifact.Clone()),
            ServiceImplementationKind.Workflow);
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = spec,
        });

        var prepare = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent with its authoring spec*");
        adapter.PrepareCalls.Should().Be(1);
        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.PreparationFailed);
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
        var legacySpec = CreateWorkflowRevisionSpec(identity, revisionId);
        legacySpec.WorkflowSpec!.CapabilityAdmissionPlan = null;
        legacySpec.WorkflowSpec.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Unspecified;

        var seed = CreateAgent(eventStore, adapter, actorId);
        await seed.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = legacySpec,
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
        record.Spec.WorkflowSpec.CapabilityAdmissionPlan.Should().BeNull();
        record.Spec.WorkflowSpec.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Unspecified);
        WorkflowServiceDeploymentPlanIntegrity.IsCompatible(
            record.PreparedArtifact,
            revisionId).Should().BeTrue();
        adapter.PrepareCalls.Should().Be(1);
        dispatchPort.Calls.Should().ContainSingle();
        var observation = dispatchPort.Calls[0].Envelope.Payload
            .Unpack<ObserveServiceInvocationRevisionsCommand>();
        observation.SourceRevisionVersion.Should().Be(4);
        observation.RevisionReadiness[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);

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
    public async Task PrepareLegacyWorkflowRevision_ShouldRejectDurableArtifact()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity("svc-legacy-durable");
        const string revisionId = "rev-legacy-durable";
        var durableArtifact = CreateWorkflowArtifact(
            identity,
            revisionId,
            ExternalCapabilityExecutionMode.Durable);
        durableArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.ExecutionMode =
            ExternalCapabilityExecutionMode.Durable;
        durableArtifact = new PreparedServiceRevisionArtifactAssembler().Assemble(durableArtifact);
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(durableArtifact.Clone()),
            ServiceImplementationKind.Workflow);
        var agent = CreateAgent(
            eventStore,
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        var legacySpec = CreateWorkflowRevisionSpec(identity, revisionId);
        legacySpec.WorkflowSpec!.CapabilityAdmissionPlan = null;
        legacySpec.WorkflowSpec.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Unspecified;

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = legacySpec,
        });

        var prepare = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inconsistent with its authoring spec*");
        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.PreparationFailed);
        agent.State.Revisions[revisionId].PreparedArtifact.Should().BeNull();
        adapter.PrepareCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(
        ExternalCapabilityExecutionMode.Unspecified,
        ExternalCapabilityExecutionMode.Interactive)]
    [InlineData(
        ExternalCapabilityExecutionMode.Interactive,
        ExternalCapabilityExecutionMode.Interactive)]
    [InlineData(
        ExternalCapabilityExecutionMode.Durable,
        ExternalCapabilityExecutionMode.Durable)]
    public async Task PrepareWorkflowRevision_ShouldAcceptAdapterGeneratedPlan_WhenSpecPlanIsMissing(
        ExternalCapabilityExecutionMode expectedExecutionMode,
        ExternalCapabilityExecutionMode artifactExecutionMode)
    {
        var identity = GAgentServiceTestKit.CreateIdentity(
            $"svc-null-plan-{expectedExecutionMode.ToString().ToLowerInvariant()}");
        var revisionId = $"rev-null-plan-{expectedExecutionMode.ToString().ToLowerInvariant()}";
        var artifact = CreateWorkflowArtifact(identity, revisionId, artifactExecutionMode);
        artifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.ExecutionMode =
            artifactExecutionMode;
        artifact = new PreparedServiceRevisionArtifactAssembler().Assemble(artifact);
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(artifact.Clone()),
            ServiceImplementationKind.Workflow);
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        var spec = CreateWorkflowRevisionSpec(identity, revisionId);
        spec.WorkflowSpec!.ExpectedExecutionMode = expectedExecutionMode;
        spec.WorkflowSpec.CapabilityAdmissionPlan = null;

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = spec,
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        var prepared = agent.State.Revisions[revisionId];
        prepared.Status.Should().Be(ServiceRevisionStatus.Prepared);
        prepared.PreparedArtifact.DeploymentPlan.WorkflowPlan.ExecutionMode.Should()
            .Be(artifactExecutionMode);
        prepared.PreparedArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan
            .Should().NotBeNull();
        adapter.PrepareCalls.Should().Be(1);
    }

    [Fact]
    public async Task PreparePublishAndReplayWorkflowRevision_ShouldAcceptAdapterNormalizedWorkflowName()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity("svc-padded-workflow-name");
        const string revisionId = "rev-padded-workflow-name";
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var spec = CreateWorkflowRevisionSpec(identity, revisionId);
        spec.WorkflowSpec!.WorkflowName = "  legacy-workflow  ";
        var artifactSpec = spec.Clone();
        artifactSpec.WorkflowSpec!.WorkflowId = revisionId;
        var artifact = new PreparedServiceRevisionArtifactAssembler().Assemble(
            WorkflowServiceRevisionArtifactBuilder.Build(
                artifactSpec,
                "legacy-workflow",
                new WorkflowAuthorizationDependencies
                {
                    ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
                },
                artifactSpec.WorkflowSpec.CapabilityAdmissionPlan));
        var expectedArtifactHash = artifact.ArtifactHash;
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(artifact.Clone()),
            ServiceImplementationKind.Workflow);
        var agent = CreateAgent(
            eventStore,
            adapter,
            actorId);

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = spec,
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

        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.Published);
        agent.State.Revisions[revisionId].PreparedArtifact.DeploymentPlan.WorkflowPlan.WorkflowName
            .Should().Be("legacy-workflow");
        agent.State.Revisions[revisionId].ArtifactHash.Should().Be(expectedArtifactHash);
        agent.State.Revisions[revisionId].PreparedArtifact.ProtocolDescriptorSet.IsEmpty.Should().BeFalse();
        adapter.PrepareCalls.Should().Be(2);

        var replayed = CreateAgent(eventStore, adapter, actorId);
        await replayed.ActivateAsync();
        replayed.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.Published);
        replayed.State.Revisions[revisionId].ArtifactHash.Should().Be(expectedArtifactHash);
        replayed.State.Revisions[revisionId].PreparedArtifact.ProtocolDescriptorSet
            .ToByteArray()
            .Should()
            .Equal(artifact.ProtocolDescriptorSet.ToByteArray());
        await replayed.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await replayed.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        adapter.PrepareCalls.Should().Be(2);
    }

    [Fact]
    public async Task PrepareWorkflowRevision_ShouldRejectArtifactNameDifferentFromTrimmedSpec()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-workflow-name-mismatch");
        const string revisionId = "rev-workflow-name-mismatch";
        var artifact = CreateWorkflowArtifact(
            identity,
            revisionId,
            ExternalCapabilityExecutionMode.Interactive);
        artifact.DeploymentPlan.WorkflowPlan.WorkflowName = "different-workflow";
        artifact = new PreparedServiceRevisionArtifactAssembler().Assemble(artifact);
        var adapter = new RecordingAdapter(
            _ => Task.FromResult(artifact.Clone()),
            ServiceImplementationKind.Workflow);
        var agent = CreateAgent(
            new InMemoryEventStore(),
            adapter,
            ServiceActorIds.RevisionCatalog(identity));
        var spec = CreateWorkflowRevisionSpec(identity, revisionId);
        spec.WorkflowSpec!.WorkflowName = "  legacy-workflow  ";
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = spec,
        });

        var prepare = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent with its authoring spec*");
        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.PreparationFailed);
        adapter.PrepareCalls.Should().Be(1);
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
    public async Task RestartAfterPublish_ShouldReplayPreparedArtifactFromCommittedState()
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
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
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
                Id = "outer-replayed-published",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = replayed.State.LastEventId,
                        Version = replayed.State.LastAppliedEventVersion,
                        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                        EventData = Any.Pack(new ServiceRevisionPublishedEvent
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
    public async Task HandleCreateRevisionAsync_ShouldBeIdempotentForEquivalentRevision()
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
        var committedVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.Revisions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldAcceptForwardWorkflowEvidenceReplayWithoutMutatingState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-create-evidence-replay");
        const string revisionId = "rev-create-evidence-replay";
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                _ => throw new InvalidOperationException("not used"),
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3);
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        var committedVersion = agent.State.LastAppliedEventVersion;
        var refreshedSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4);

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = refreshedSpec,
        });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.Revisions[revisionId].Spec.Should().BeEquivalentTo(originalSpec);
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectBackwardWorkflowEvidenceReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-create-evidence-rollback");
        const string revisionId = "rev-create-evidence-rollback";
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                _ => throw new InvalidOperationException("not used"),
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = CreateExplicitRequestWorkflowRevisionSpec(
                identity,
                revisionId,
                sourceVersion: 4),
        });
        var committedVersion = agent.State.LastAppliedEventVersion;

        var replay = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = CreateExplicitRequestWorkflowRevisionSpec(
                identity,
                revisionId,
                sourceVersion: 3),
        });

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*version moved backwards*");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldAtomicallyRefreshFailedWorkflowAdmissionEvidence_AndPrepare()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-retry");
        const string revisionId = "rev-explicit-retry";
        var eventStore = new InMemoryEventStore();
        var prepareRequests = new List<PrepareServiceRevisionRequest>();
        var agent = CreateAgent(
            eventStore,
            new RecordingAdapter(
                request =>
                {
                    prepareRequests.Add(new PrepareServiceRevisionRequest
                    {
                        ServiceKey = request.ServiceKey,
                        Spec = request.Spec.Clone(),
                    });
                    if (prepareRequests.Count == 1)
                        throw new InvalidOperationException("admission evidence expired");

                    return Task.FromResult(
                        CreateExplicitRequestWorkflowArtifact(request.Spec));
                },
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3,
            observedAt: new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            freshUntil: new DateTimeOffset(2026, 8, 17, 0, 5, 0, TimeSpan.Zero));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        var prepare = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        await prepare.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("admission evidence expired");
        agent.State.Revisions[revisionId].Status.Should()
            .Be(ServiceRevisionStatus.PreparationFailed);
        var committedVersion = agent.State.LastAppliedEventVersion;

        var refreshedSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4,
            observedAt: new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero),
            freshUntil: new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero));
        originalSpec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps[0].FreshUntil
            .ToDateTimeOffset().Should().BeBefore(
                refreshedSpec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps[0].ObservedAt
                    .ToDateTimeOffset());
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = refreshedSpec,
        });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion + 1);
        agent.State.Revisions.Should().ContainSingle();
        agent.State.Revisions[revisionId].Spec.Should().BeEquivalentTo(refreshedSpec);
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Prepared);
        var refreshedEvents = await eventStore.GetEventsAsync(
            ServiceActorIds.RevisionCatalog(identity));
        refreshedEvents.Should().HaveCount(3);
        var refreshedEvent = refreshedEvents[^1].EventData
            .Unpack<ServiceRevisionAdmissionEvidenceRefreshedEvent>();
        refreshedEvent.RevisionId.Should().Be(revisionId);
        refreshedEvent.Spec.Should().BeEquivalentTo(refreshedSpec);
        refreshedEvent.PreparedArtifact.Should().BeEquivalentTo(
            agent.State.Revisions[revisionId].PreparedArtifact);

        prepareRequests.Should().HaveCount(2);
        prepareRequests[1].Spec.WorkflowSpec.CapabilityAdmissionPlan.Should()
            .BeEquivalentTo(refreshedSpec.WorkflowSpec.CapabilityAdmissionPlan);
        (await eventStore.GetEventsAsync(ServiceActorIds.RevisionCatalog(identity)))
            .Should().HaveCount(3);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldAtomicallyRefreshPreparedWorkflowEvidence_AndPublishWithExactFence()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-prepared-refresh");
        const string revisionId = "rev-explicit-prepared-refresh";
        var eventStore = new InMemoryEventStore();
        var prepareRequests = new List<PrepareServiceRevisionRequest>();
        var refreshTime = new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero);
        var agent = CreateAgent(
            eventStore,
            new RecordingAdapter(
                request =>
                {
                    prepareRequests.Add(new PrepareServiceRevisionRequest
                    {
                        ServiceKey = request.ServiceKey,
                        Spec = request.Spec.Clone(),
                    });
                    if (prepareRequests.Count > 1 &&
                        request.Spec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps.Any(
                            source => source.FreshUntil.ToDateTimeOffset() <= refreshTime))
                    {
                        throw new InvalidOperationException("admission evidence expired");
                    }

                    return Task.FromResult(
                        CreateExplicitRequestWorkflowArtifact(request.Spec));
                },
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3,
            observedAt: refreshTime.AddHours(-2),
            freshUntil: refreshTime.AddHours(-1));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });
        var committedRecord = agent.State.Revisions[revisionId].Clone();
        var committedVersion = agent.State.LastAppliedEventVersion;
        var committedEventCount = (await eventStore.GetEventsAsync(
            ServiceActorIds.RevisionCatalog(identity))).Count;
        var refreshedSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4,
            observedAt: refreshTime,
            freshUntil: refreshTime.AddHours(1));

        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = refreshedSpec.Clone(),
        });

        var refreshedRecord = agent.State.Revisions[revisionId];
        refreshedRecord.Status.Should().Be(ServiceRevisionStatus.Prepared);
        refreshedRecord.Spec.Should().BeEquivalentTo(refreshedSpec);
        refreshedRecord.PreparedArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan
            .Should().BeEquivalentTo(refreshedSpec.WorkflowSpec.CapabilityAdmissionPlan);
        refreshedRecord.ArtifactHash.Should().Be(refreshedRecord.PreparedArtifact.ArtifactHash);
        refreshedRecord.ArtifactHash.Should().NotBe(committedRecord.ArtifactHash);
        refreshedRecord.PreparedAt.Should().BeEquivalentTo(committedRecord.PreparedAt);
        refreshedRecord.Endpoints.Should().Equal(committedRecord.Endpoints);
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion + 1);
        var committedEvents = await eventStore.GetEventsAsync(
            ServiceActorIds.RevisionCatalog(identity));
        committedEvents.Should().HaveCount(committedEventCount + 1);
        var refreshedEvent = committedEvents[^1].EventData
            .Unpack<ServiceRevisionAdmissionEvidenceRefreshedEvent>();
        refreshedEvent.PreviousArtifactHash.Should().Be(committedRecord.ArtifactHash);
        refreshedEvent.Spec.Should().BeEquivalentTo(refreshedSpec);
        refreshedEvent.PreparedArtifact.Should().BeEquivalentTo(refreshedRecord.PreparedArtifact);

        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PublicationSpec = refreshedSpec.Clone(),
        });

        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Published);
        prepareRequests.Should().HaveCount(3);
        prepareRequests[0].Spec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps
            .Should().OnlyContain(source =>
                source.FreshUntil.ToDateTimeOffset() <= refreshTime);
        prepareRequests.Skip(1).Should().OnlyContain(request =>
            request.Spec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps.All(source =>
                source.FreshUntil.ToDateTimeOffset() > refreshTime));
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRejectNewFence_AfterPreparedEvidenceRefreshFails()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-refresh-failure");
        const string revisionId = "rev-explicit-refresh-failure";
        var prepareCalls = 0;
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                request =>
                {
                    prepareCalls++;
                    if (prepareCalls == 2)
                        throw new InvalidOperationException("refreshed admission evidence rejected");

                    return Task.FromResult(
                        CreateExplicitRequestWorkflowArtifact(request.Spec));
                },
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3);
        var refreshedSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4);
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = originalSpec.Clone(),
        });
        var originalPreparedRecord = agent.State.Revisions[revisionId].Clone();

        var refresh = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = refreshedSpec.Clone(),
        });
        await refresh.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("refreshed admission evidence rejected");

        agent.State.Revisions[revisionId].Should().BeEquivalentTo(originalPreparedRecord);
        var publish = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PublicationSpec = refreshedSpec.Clone(),
        });
        await publish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publication_spec does not match*");
        prepareCalls.Should().Be(2);
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Prepared);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldRejectEmptyPreparedArtifactHash_BeforeRefreshAdapterRuns()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-empty-artifact-hash");
        const string revisionId = "rev-explicit-empty-artifact-hash";
        var prepareCalls = 0;
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                request =>
                {
                    prepareCalls++;
                    return Task.FromResult(
                        CreateExplicitRequestWorkflowArtifact(request.Spec));
                },
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3);
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = originalSpec.Clone(),
        });
        agent.State.Revisions[revisionId].PreparedArtifact.ArtifactHash = string.Empty;

        var refresh = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = CreateExplicitRequestWorkflowRevisionSpec(
                identity,
                revisionId,
                sourceVersion: 4),
        });

        await refresh.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact is inconsistent*");
        prepareCalls.Should().Be(1);
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Prepared);
    }

    [Fact]
    public async Task PrepareAndPublish_ShouldKeepPublishedWorkflowArtifactImmutable_ForSemanticEvidenceReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-published");
        const string revisionId = "rev-explicit-published";
        var eventStore = new InMemoryEventStore();
        var prepareCalls = 0;
        var agent = CreateAgent(
            eventStore,
            new RecordingAdapter(
                request =>
                {
                    prepareCalls++;
                    return Task.FromResult(
                        CreateExplicitRequestWorkflowArtifact(request.Spec));
                },
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 3);
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
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
        var publishedRecord = agent.State.Revisions[revisionId].Clone();
        var committedVersion = agent.State.LastAppliedEventVersion;
        var committedEventCount = (await eventStore.GetEventsAsync(
            ServiceActorIds.RevisionCatalog(identity))).Count;

        var replaySpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4);
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = replaySpec.Clone(),
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PublicationSpec = replaySpec.Clone(),
        });

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.Revisions[revisionId].Should().BeEquivalentTo(publishedRecord);
        prepareCalls.Should().Be(2);
        (await eventStore.GetEventsAsync(ServiceActorIds.RevisionCatalog(identity)))
            .Should().HaveCount(committedEventCount);

        var rollbackPublish = () => agent.HandlePublishRevisionAsync(
            new PublishServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
                PublicationSpec = CreateExplicitRequestWorkflowRevisionSpec(
                    identity,
                    revisionId,
                    sourceVersion: 2),
            });
        await rollbackPublish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*moved backwards*");

        var conflictingSpec = replaySpec.Clone();
        conflictingSpec.WorkflowSpec.WorkflowName = "different-workflow";
        var conflictingPublish = () => agent.HandlePublishRevisionAsync(
            new PublishServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = revisionId,
                PublicationSpec = conflictingSpec,
            });
        await conflictingPublish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publication_spec does not match*");
        agent.State.Revisions[revisionId].Should().BeEquivalentTo(publishedRecord);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldRejectRetiredWorkflowEvidenceRefresh()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-retired");
        const string revisionId = "rev-explicit-retired";
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                _ => throw new InvalidOperationException("not used"),
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = CreateExplicitRequestWorkflowRevisionSpec(
                identity,
                revisionId,
                sourceVersion: 3),
        });
        await agent.HandleRetireRevisionAsync(new RetireServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = CreateExplicitRequestWorkflowRevisionSpec(
                identity,
                revisionId,
                sourceVersion: 4),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has been retired*");
        agent.State.Revisions[revisionId].Status.Should().Be(ServiceRevisionStatus.Retired);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("observed-at")]
    [InlineData("fresh-until")]
    [InlineData("same-version-content")]
    public async Task HandlePrepareRevisionAsync_ShouldRejectWorkflowAdmissionEvidenceRollback(
        string rollback)
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-rollback");
        const string revisionId = "rev-explicit-rollback";
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                _ => throw new InvalidOperationException("not used"),
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        var originalSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4);
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = originalSpec,
        });
        var rollbackSpec = originalSpec.Clone();
        var source = rollbackSpec.WorkflowSpec.CapabilityAdmissionPlan.SourceStamps[0];
        switch (rollback)
        {
            case "version":
                source.SourceVersion--;
                break;
            case "observed-at":
                source.ObservedAt = Timestamp.FromDateTimeOffset(
                    source.ObservedAt.ToDateTimeOffset().AddMinutes(-1));
                break;
            case "fresh-until":
                source.FreshUntil = Timestamp.FromDateTimeOffset(
                    source.FreshUntil.ToDateTimeOffset().AddMinutes(-1));
                break;
            case "same-version-content":
                source.ContentDigest = "different-content-at-same-version";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rollback));
        }
        rollbackSpec.WorkflowSpec.CapabilityAdmissionPlan.AdmissionDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(
                rollbackSpec.WorkflowSpec.CapabilityAdmissionPlan);

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = rollbackSpec,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(rollback == "same-version-content"
                ? "*same version*"
                : "*moved backwards*");
        agent.State.Revisions[revisionId].Spec.Should().BeEquivalentTo(originalSpec);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("grant")]
    [InlineData("durable-owner")]
    [InlineData("definition")]
    [InlineData("source-kind")]
    [InlineData("source-id")]
    public async Task HandlePrepareRevisionAsync_ShouldRejectWorkflowContractDrift_WhenAdmissionEvidenceIsRefreshed(
        string changedContract)
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-explicit-conflict");
        const string revisionId = "rev-explicit-conflict";
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new RecordingAdapter(
                _ => throw new InvalidOperationException("not used"),
                ServiceImplementationKind.Workflow),
            ServiceActorIds.RevisionCatalog(identity));
        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = CreateExplicitRequestWorkflowRevisionSpec(identity, revisionId, sourceVersion: 3),
        });
        var conflictingSpec = CreateExplicitRequestWorkflowRevisionSpec(
            identity,
            revisionId,
            sourceVersion: 4);
        var plan = conflictingSpec.WorkflowSpec.CapabilityAdmissionPlan;
        switch (changedContract)
        {
            case "capability":
                plan.InvocationAdmissions[0].Capability.NyxIdUserRequest.ServiceSlugSnapshot =
                    "different-service";
                break;
            case "grant":
                plan.InvocationAdmissions[0].NyxIdExplicitRequestGrant.GrantorOwnerSubject =
                    "different-grantor";
                break;
            case "durable-owner":
                plan.DurableAuthorizationOwner.OwnerSubject = "different-owner";
                break;
            case "definition":
                plan.DefinitionDigest = "different-definition";
                break;
            case "source-kind":
                plan.SourceStamps[0].SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog;
                break;
            case "source-id":
                plan.SourceStamps[0].SourceId = "nyxid-keys:different-owner";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedContract));
        }
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = conflictingSpec,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*preparation_spec conflicts*");
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectConflictingDuplicateRevision()
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
        var conflictingSpec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1");
        conflictingSpec.StaticSpec.AgentKind = "tests.conflicting-agent";

        var act = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = conflictingSpec,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*conflicting spec*");
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
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
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

    private static ServiceRevisionSpec CreateExplicitRequestWorkflowRevisionSpec(
        ServiceIdentity identity,
        string revisionId,
        long sourceVersion,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? freshUntil = null)
    {
        const string workflowId = "wf-explicit-request";
        const string workflowYaml = """
            name: explicit-request
            steps:
              - id: request
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: usvc-alpha
                    method: GET
                    path_template: /api/resources/{resource_id}
                    body_mode: none
                    response_mode: text
                parameters:
                  tool: nyxid_proxy
                  arguments: '{}'
            """;
        const string callSiteId = "explicit-request/request";
        const string serviceSlug = "service-alpha";
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
            Risk = NyxIdOperationRisk.ReadOnly,
        };
        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = new NyxIdExplicitRequestGrant
        {
            CallSiteId = callSiteId,
            RequestContractDigest = requestContractDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "owner-alpha",
            Risk = NyxIdOperationRisk.ReadOnly,
            WorkflowId = workflowId,
            RevisionId = revisionId,
        };
        grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        var capability = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = request,
                ServiceSlugSnapshot = serviceSlug,
                ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                    .ComputeNyxIdExplicitRequestProofDigest(requestContractDigest, serviceSlug),
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.ReadOnly,
                    Approval = NyxIdOperationApproval.None,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes = { ExternalCapabilityExecutionMode.Durable },
                },
            },
        };
        capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            DefinitionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                workflowYaml,
                inlineWorkflowYamls: null,
                workflowId,
                revisionId),
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
            InvocationAdmissions =
            {
                new WorkflowCapabilityInvocationAdmission
                {
                    CallSiteId = callSiteId,
                    Capability = capability,
                    NyxIdExplicitRequestGrant = grant,
                },
            },
            SourceStamps =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                    SourceId = "nyxid-keys:owner-alpha",
                    SourceVersion = sourceVersion,
                    ObservedAt = Timestamp.FromDateTimeOffset(
                        observedAt ??
                        new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero)
                            .AddMinutes(sourceVersion)),
                    FreshUntil = Timestamp.FromDateTimeOffset(
                        freshUntil ??
                        new DateTimeOffset(2026, 8, 17, 1, 5, 0, TimeSpan.Zero)
                            .AddMinutes(sourceVersion)),
                    ContentDigest = $"source-{sourceVersion}",
                },
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                    SourceId = NyxIdAuthorizationCatalogActorIds.Build(
                        new AuthorizationOwnerIdentity
                        {
                            Authority = NyxIdAuthorizationAuthorities.NyxId,
                            OwnerKind = AuthorizationOwnerKind.Personal,
                            OwnerSubject = "owner-alpha",
                        }),
                    SourceVersion = sourceVersion,
                    ObservedAt = Timestamp.FromDateTimeOffset(
                        observedAt ??
                        new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero)
                            .AddMinutes(sourceVersion)),
                    FreshUntil = Timestamp.FromDateTimeOffset(
                        freshUntil ??
                        new DateTimeOffset(2026, 8, 17, 1, 5, 0, TimeSpan.Zero)
                            .AddMinutes(sourceVersion)),
                    ContentDigest = $"authorization-catalog-{sourceVersion}",
                },
            },
        };
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);

        return new ServiceRevisionSpec
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                WorkflowId = workflowId,
                WorkflowName = "explicit-request",
                WorkflowYaml = workflowYaml,
                DefinitionActorId = "workflow-definition-explicit-request",
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
                CapabilityAdmissionPlan = plan,
            },
        };
    }

    private static PreparedServiceRevisionArtifact CreateExplicitRequestWorkflowArtifact(
        ServiceRevisionSpec spec)
    {
        var workflowSpec = spec.WorkflowSpec
            ?? throw new InvalidOperationException("workflow spec is required");
        var plan = workflowSpec.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException("workflow capability admission plan is required");
        return WorkflowServiceRevisionArtifactBuilder.Build(
            spec,
            workflowSpec.WorkflowName,
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
            },
            plan);
    }

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
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
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
