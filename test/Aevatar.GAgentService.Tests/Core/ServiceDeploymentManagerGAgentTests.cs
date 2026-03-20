using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDeploymentManagerGAgentTests
{
    [Fact]
    public async Task HandleActivateAsync_ShouldPersistAndReplayDeploymentRecord()
    {
        var eventStore = new InMemoryEventStore();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
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

        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].RevisionId.Should().Be("r1");
        agent.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, artifactStore, activator, actorId);
        await replayed.ActivateAsync();
        replayed.State.Deployments.Should().ContainKey("dep-r1");
        replayed.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldKeepMultipleActiveDeploymentsForDifferentRevisions()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r2", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r2"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r2", "actor-r2", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), artifactStore, activator, ServiceActorIds.Deployment(identity));

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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), artifactStore, activator, ServiceActorIds.Deployment(identity));

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
    public async Task HandleActivateAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new ConfiguredServiceRevisionArtifactStore(),
            new RecordingRuntimeActivator(),
            ServiceActorIds.Deployment(identity));

        var act = () => agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "missing",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Prepared artifact was not found*");
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldDeactivateSpecificActiveDeployment()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), artifactStore, activator, ServiceActorIds.Deployment(identity));

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

        activator.DeactivateRequests.Should().ContainSingle(x => x.DeploymentId == "dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Deactivated);
    }

    [Fact]
    public async Task HandleDeactivateAsync_ShouldIgnoreUnknownOrInactiveDeployment_WhenStateAlreadyExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), artifactStore, activator, ServiceActorIds.Deployment(identity));

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
            new ConfiguredServiceRevisionArtifactStore(),
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
            new ConfiguredServiceRevisionArtifactStore(),
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var activator = new RecordingRuntimeActivator();
        activator.ActivationResults.Enqueue(new ServiceRuntimeActivationResult("dep-r1", "actor-r1", "active"));
        var agent = CreateAgent(new InMemoryEventStore(), artifactStore, activator, ServiceActorIds.Deployment(identity));
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

    private static ServiceDeploymentManagerGAgent CreateAgent(
        InMemoryEventStore eventStore,
        ConfiguredServiceRevisionArtifactStore artifactStore,
        RecordingRuntimeActivator activator,
        string actorId)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceDeploymentManagerGAgent, ServiceDeploymentState>(
            eventStore,
            actorId,
            () => new ServiceDeploymentManagerGAgent(
                artifactStore,
                new AlwaysReadyCapabilityViewReader(),
                new AllowActivationAdmissionEvaluator(),
                activator));
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

        public List<ServiceRuntimeDeactivationRequest> DeactivateRequests { get; } = [];

        public Task<ServiceRuntimeActivationResult> ActivateAsync(
            ServiceRuntimeActivationRequest request,
            CancellationToken ct = default)
        {
            ActivationRequests.Add(request);
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
