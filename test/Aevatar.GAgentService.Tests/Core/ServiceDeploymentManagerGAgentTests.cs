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
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDeploymentManagerGAgentTests
{
    [Fact]
    public async Task HandleActivateAsync_ShouldPersistAndReplayDeploymentRecord()
    {
        var eventStore = new InMemoryEventStore();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var identity = GAgentServiceTestKit.CreateIdentity();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
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

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, revisionCatalog, activator, actorId);
        await replayed.ActivateAsync();
        replayed.State.Deployments.Should().ContainKey("dep-r1");
        replayed.State.Deployments["dep-r1"].PrimaryActorId.Should().Be("actor-r1");
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

        // Projection not yet materialized -> tolerated, re-armed (no throw, no serving-set write yet).
        await agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        activator.ActivationRequests.Should().BeEmpty("activation must wait until the prepared artifact is visible");
        dispatchPort.Commands.Should().BeEmpty("serving set must not be written before activation succeeds");
        scheduler.ScheduledTimeouts.Should().ContainSingle("activation should re-arm a bounded self-continuation");
        var rearmed = scheduler.ScheduledTimeouts[0].Payload.Unpack<ActivateServiceRevisionCommand>();
        rearmed.RevisionId.Should().Be("r1");
        rearmed.ActivationDeadlineAt.Should().NotBeNull("the bounded retry deadline must be stamped onto the re-armed command");

        // Projection catches up; the re-fired continuation now succeeds and writes the serving set.
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));

        await agent.HandleActivateAsync(rearmed);

        activator.ActivationRequests.Should().ContainSingle();
        agent.State.Deployments.Should().ContainKey("dep-r1");
        agent.State.Deployments["dep-r1"].Status.Should().Be(ServiceDeploymentStatus.Active);
        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].actorId.Should().Be(ServiceActorIds.ServingSet(identity));
        dispatchPort.Commands[0].command.Targets[0].DeploymentId.Should().Be("dep-r1");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldFailTerminally_WhenProjectionLagExceedsDeadline()
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

        var act = () => agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
            // Deadline already in the past -> the retry budget is exhausted, so the honest failure surfaces.
            ActivationDeadlineAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found before the activation deadline*");
        scheduler.ScheduledTimeouts.Should().BeEmpty("an exhausted budget must not keep re-arming");
    }

    [Fact]
    public async Task HandleActivateAsync_ShouldFailTerminally_WhenRevisionPreparationFailed()
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

        var act = () => agent.HandleActivateAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*failed preparation*");
        scheduler.ScheduledTimeouts.Should().BeEmpty("a terminally failed revision must not be re-armed");
    }

    [Fact]
    public void GetRequiredPreparedArtifact_ShouldThrow_WhenRevisionMissing()
    {
        // The terminal "missing prepared artifact" failure now lives in the snapshot extension that other
        // (non-activation) callers still use; activation itself tolerates the same gap as projection lag.
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
        var agent = CreateAgent(new InMemoryEventStore(), revisionCatalog, activator, ServiceActorIds.Deployment(identity));

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

    private static ServiceDeploymentManagerGAgent CreateAgent(
        InMemoryEventStore eventStore,
        IServiceRevisionCatalogQueryReader revisionCatalog,
        RecordingRuntimeActivator activator,
        string actorId,
        RecordingDispatchPort? dispatchPort = null,
        RecordingCallbackScheduler? scheduler = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceDeploymentManagerGAgent, ServiceDeploymentState>(
            eventStore,
            actorId,
            () => new ServiceDeploymentManagerGAgent(
                dispatchPort ?? new RecordingDispatchPort(),
                revisionCatalog,
                new AlwaysReadyCapabilityViewReader(),
                new AllowActivationAdmissionEvaluator(),
                activator),
            scheduler == null
                ? null
                : services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
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

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<EventEnvelope> ScheduledTimeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
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
}
