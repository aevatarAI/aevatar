using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDeploymentManagerGAgentTests
{
    [Fact]
    public async Task HandleActivateAsync_ShouldPersistAndReplayActiveDeployment()
    {
        var eventStore = new InMemoryEventStore();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var actorId = ServiceActorIds.Deployment(identity);
        var agent = CreateAgent(eventStore, artifactStore, activator, actorId);
        await agent.ActivateAsync();

        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.ActiveRevisionId.Should().Be("r1");
        agent.State.ActiveDeploymentId.Should().Be("dep-r1");
        agent.State.PrimaryActorId.Should().Be("actor-r1");

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, artifactStore, activator, actorId);
        await replayed.ActivateAsync();
        replayed.State.ActiveRevisionId.Should().Be("r1");
        replayed.State.PrimaryActorId.Should().Be("actor-r1");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldDeactivatePreviousRuntime_BeforeSwitchingRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r2"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r2", "actor-r2", "active"));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            artifactStore,
            activator,
            ServiceActorIds.Deployment(identity));

        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        activator.DeactivateRequests.Should().ContainSingle();
        activator.DeactivateRequests[0].RevisionId.Should().Be("r1");
        activator.DeactivateRequests[0].PrimaryActorId.Should().Be("actor-r1");
        agent.State.ActiveRevisionId.Should().Be("r2");
        agent.State.PrimaryActorId.Should().Be("actor-r2");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));

        var act = () => agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "missing",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Prepared artifact was not found*");
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldIssueDeactivationAgain_WhenActiveRuntimeCoordinatesRemainRecorded()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            artifactStore,
            activator,
            ServiceActorIds.Deployment(identity));

        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
        });
        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
        });

        activator.DeactivateRequests.Should().HaveCount(2);
        agent.State.Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldDeactivateCurrentRuntime_WhenActiveDeploymentExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            artifactStore,
            activator,
            ServiceActorIds.Deployment(identity));

        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
        });

        activator.DeactivateRequests.Should().ContainSingle(x => x.DeploymentId == "dep-r1");
        agent.State.Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldRejectBlankRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));

        var act = () => agent.HandleActivateAsync(new ActivateServingRevisionCommand
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
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(
            new InMemoryEventStore(),
            artifactStore,
            activator,
            ServiceActorIds.Deployment(identity));
        await agent.HandleActivateAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var act = () => agent.HandleDeactivateAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = otherIdentity.Clone(),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    private static ServiceDeploymentManagerGAgent CreateAgent(
        InMemoryEventStore eventStore,
        InMemoryServiceRevisionArtifactStore artifactStore,
        RecordingRuntimeActivator activator,
        string actorId)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceDeploymentManagerGAgent, ServiceDeploymentState>(
            eventStore,
            actorId,
            () => new ServiceDeploymentManagerGAgent(artifactStore, activator));
    }

    private sealed class RecordingRuntimeActivator : IServiceRuntimeActivator
    {
        public Queue<ServiceRuntimeActivationResult> ActivationResults { get; } = new();

        public List<ServiceRuntimeDeactivationRequest> DeactivateRequests { get; } = [];

        public Task<ServiceRuntimeActivationResult> ActivateAsync(
            ServiceRuntimeActivationRequest request,
            CancellationToken ct = default)
        {
            if (ActivationResults.Count == 0)
                throw new InvalidOperationException("No activation result configured.");

            return Task.FromResult(ActivationResults.Dequeue());
        }

        public Task DeactivateAsync(ServiceRuntimeDeactivationRequest request, CancellationToken ct = default)
        {
            DeactivateRequests.Add(request);
            return Task.CompletedTask;
        }
    }
}
