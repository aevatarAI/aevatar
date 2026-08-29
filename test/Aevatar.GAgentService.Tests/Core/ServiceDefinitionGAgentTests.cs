using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDefinitionGAgentTests
{
    [Fact]
    public async Task HandleCreateAsync_ShouldPersistAndReplayDefinitionState()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        agent.State.Spec.Identity.ServiceId.Should().Be("svc");
        agent.State.Spec.DisplayName.Should().Be("Service");

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await replayed.ActivateAsync();

        replayed.State.Spec.Identity.ServiceId.Should().Be("svc");
        replayed.State.Spec.DisplayName.Should().Be("Service");
        replayed.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldIgnoreInlineExternalExposure()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        var spec = GAgentServiceTestKit.CreateDefinitionSpec(identity);
        spec.ExternalExposure = new ExternalExposure
        {
            NyxidSlug = "aevatar-orders",
            RegisteredAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-06-11T01:02:03+00:00")),
        };

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = spec,
        });
        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await replayed.ActivateAsync();

        replayed.State.Spec.ExternalExposure.Should().BeNull();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldKeepInlineExposureDesiredIntentOnly()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        var spec = GAgentServiceTestKit.CreateDefinitionSpec(identity);
        spec.ExternalExposure = new ExternalExposure
        {
            ExposureDesired = true,
            NyxidSlug = "caller-supplied-slug",
            NyxidServiceId = "caller-supplied-id",
            LastError = "caller-supplied-error",
        };

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = spec,
        });
        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await replayed.ActivateAsync();

        replayed.State.Spec.ExternalExposure.Should().NotBeNull();
        replayed.State.Spec.ExternalExposure.ExposureDesired.Should().BeTrue();
        replayed.State.Spec.ExternalExposure.NyxidSlug.Should().BeEmpty();
        replayed.State.Spec.ExternalExposure.NyxidServiceId.Should().BeEmpty();
        replayed.State.Spec.ExternalExposure.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectDuplicateCreate_AndKeepOriginalState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        agent.State.Spec.DisplayName.Should().Be("Service");
        agent.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldMutateExistingDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(dispatchPort));

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var updatedSpec = GAgentServiceTestKit.CreateDefinitionSpec(
            identity,
            GAgentServiceTestKit.CreateEndpointSpec(endpointId: "chat", kind: ServiceEndpointKind.Chat, requestTypeUrl: "type.googleapis.com/test.chat"));
        updatedSpec.DisplayName = "Updated";

        await agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = updatedSpec,
        });
        agent.State.Spec.DisplayName.Should().Be("Updated");
        agent.State.Spec.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        agent.State.DefaultServingRevisionId.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().Be(2);
        dispatchPort.Calls.Should().HaveCount(2);
        dispatchPort.Calls.Should().OnlyContain(x =>
            x.ActorId == ServiceActorIds.InvocationCatalog(identity));
        var updateObservation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationCatalogCommand>();
        updateObservation.SourceCatalogVersion.Should().Be(2);
        updateObservation.Identity.Should().BeEquivalentTo(identity);
        updateObservation.ServiceEndpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        updateObservation.ServiceEndpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);
        updateObservation.ServiceEndpoints[0].RequestTypeUrl.Should().Be("type.googleapis.com/test.chat");
    }

    [Fact]
    public async Task RefreshInvocationCatalogObservation_ShouldRedispatchCommittedDefinitionWithoutMutatingState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity("svc-refresh-definition");
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(dispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
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
            .Unpack<ObserveServiceInvocationCatalogCommand>();
        observation.Identity.Should().BeEquivalentTo(identity);
        observation.SourceCatalogVersion.Should().Be(committedVersion);
        observation.ServiceEndpoints.Should().ContainSingle();
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldCommitOnceAndReplayStableAck()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            () => new ServiceDefinitionGAgent(dispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var command = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
            OperationId = "operation-default-r2",
            CommandId = "command-default-r2",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-r2",
            DeploymentId = "deployment-r2",
            ServingGeneration = 7,
        };

        await agent.HandleSetDefaultServingRevisionAsync(command);

        agent.State.DefaultServingRevisionId.Should().Be("r2");
        var operation = agent.State.DefaultServingRevisionOperations
            .Should().ContainKey(command.OperationId).WhoseValue;
        operation.CommandId.Should().Be(command.CommandId);
        operation.DeploymentId.Should().Be(command.DeploymentId);
        operation.CommittedAt.Should().NotBeNull();
        operation.Disposition.Should().Be(DefaultServingRevisionCommitDisposition.Applied);
        var committedVersion = agent.State.LastAppliedEventVersion;
        var firstAckEnvelope = dispatchPort.Calls
            .Single(x => x.Envelope.Payload.Is(DefaultServingRevisionCommittedAck.Descriptor))
            .Envelope;
        firstAckEnvelope.Route.PublisherActorId.Should().Be(actorId);
        firstAckEnvelope.Route.Direct.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));

        await agent.HandleSetDefaultServingRevisionAsync(command.Clone());

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        var ackEnvelopes = dispatchPort.Calls
            .Where(x => x.Envelope.Payload.Is(DefaultServingRevisionCommittedAck.Descriptor))
            .Select(x => x.Envelope)
            .ToArray();
        ackEnvelopes.Should().HaveCount(2);
        ackEnvelopes.Select(x => x.Id).Should().OnlyContain(x => x == firstAckEnvelope.Id);
        ackEnvelopes
            .Select(x => x.Payload.Unpack<DefaultServingRevisionCommittedAck>())
            .Should().OnlyContain(x =>
                x.OperationId == command.OperationId &&
                x.CommandId == command.CommandId &&
                x.Disposition == DefaultServingRevisionCommitDisposition.Applied &&
                x.CommittedAt.Equals(operation.CommittedAt));

        var conflicting = command.Clone();
        conflicting.DeploymentId = "deployment-conflict";
        var act = () => agent.HandleSetDefaultServingRevisionAsync(conflicting);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*conflicts with its committed request*");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldAckBeforeInvocationObservationFailure()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new SelectiveFailingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(dispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        dispatchPort.Calls.Clear();
        dispatchPort.FailInvocationCatalogObservations = true;
        var command = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
            OperationId = "operation-default-r2",
            CommandId = "command-default-r2",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-r2",
            DeploymentId = "deployment-r2",
            ServingGeneration = 7,
        };

        var act = () => agent.HandleSetDefaultServingRevisionAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("invocation catalog observation dispatch failed");
        agent.State.DefaultServingRevisionId.Should().Be("r2");
        agent.State.DefaultServingRevisionOperations.Should().ContainKey(command.OperationId);
        dispatchPort.Calls.Select(call => call.Envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new DefaultServingRevisionCommittedAck()).TypeUrl,
            Any.Pack(new ObserveServiceInvocationCatalogCommand()).TypeUrl);

        dispatchPort.FailInvocationCatalogObservations = false;
        await agent.HandleSetDefaultServingRevisionAsync(command.Clone());

        dispatchPort.Calls.Count(call =>
                call.Envelope.Payload.Is(DefaultServingRevisionCommittedAck.Descriptor))
            .Should().Be(2);
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldFenceStaleGenerationAcrossReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            () => new ServiceDefinitionGAgent(dispatchPort));
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var newest = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r-newest",
            OperationId = "operation-generation-8",
            CommandId = "command-generation-8",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-generation-8",
            DeploymentId = "deployment-generation-8",
            ServingGeneration = 8,
        };
        var stale = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r-stale",
            OperationId = "operation-generation-7",
            CommandId = "command-generation-7",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-generation-7",
            DeploymentId = "deployment-generation-7",
            ServingGeneration = 7,
        };

        await agent.HandleSetDefaultServingRevisionAsync(newest);
        var committedVersion = agent.State.LastAppliedEventVersion;
        await agent.HandleSetDefaultServingRevisionAsync(stale);

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion + 1);
        agent.State.DefaultServingRevisionId.Should().Be("r-newest");
        agent.State.DefaultServingGeneration.Should().Be(8);
        agent.State.DefaultServingRevisionOperations.Should().ContainKey(newest.OperationId);
        var superseded = agent.State.DefaultServingRevisionOperations
            .Should().ContainKey(stale.OperationId).WhoseValue;
        superseded.Disposition.Should().Be(DefaultServingRevisionCommitDisposition.Superseded);
        superseded.SupersededByGeneration.Should().Be(8);
        var acks = dispatchPort.Calls
            .Where(call => call.Envelope.Payload.Is(DefaultServingRevisionCommittedAck.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<DefaultServingRevisionCommittedAck>())
            .ToArray();
        acks.Should().HaveCount(2);
        var staleAck = acks.Single(ack => ack.OperationId == stale.OperationId);
        staleAck.Disposition.Should().Be(DefaultServingRevisionCommitDisposition.Superseded);
        staleAck.SupersededByGeneration.Should().Be(8);
        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await replayed.ActivateAsync();

        replayed.State.DefaultServingRevisionId.Should().Be("r-newest");
        replayed.State.DefaultServingGeneration.Should().Be(8);
        replayed.State.DefaultServingRevisionOperations[stale.OperationId].Disposition.Should()
            .Be(DefaultServingRevisionCommitDisposition.Superseded);
        replayed.State.DefaultServingRevisionOperations[stale.OperationId].SupersededByGeneration.Should().Be(8);
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldBoundOperationHistoryAndFencePrunedReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        for (var generation = 1; generation <= 65; generation++)
        {
            await agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = $"r-{generation}",
                OperationId = $"operation-{generation}",
                CommandId = $"command-{generation}",
                ReplyActorId = ServiceActorIds.Deployment(identity),
                ActivationAttemptId = $"attempt-{generation}",
                DeploymentId = $"deployment-{generation}",
                ServingGeneration = generation,
            });
        }

        agent.State.DefaultServingRevisionOperations.Should().HaveCount(64);
        agent.State.DefaultServingRevisionOperations.Should().NotContainKey("operation-1");
        agent.State.DefaultServingRevisionId.Should().Be("r-65");
        agent.State.DefaultServingGeneration.Should().Be(65);
        await agent.DeactivateAsync();

        var replayDispatch = new RecordingActorDispatchPort();
        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            () => new ServiceDefinitionGAgent(replayDispatch));
        await replayed.ActivateAsync();
        await replayed.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r-1",
            OperationId = "operation-1",
            CommandId = "command-1",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-1",
            DeploymentId = "deployment-1",
            ServingGeneration = 1,
        });

        replayed.State.DefaultServingRevisionOperations.Should().HaveCount(64);
        replayed.State.DefaultServingRevisionOperations.Should().NotContainKey("operation-1");
        replayed.State.DefaultServingRevisionId.Should().Be("r-65");
        replayed.State.DefaultServingGeneration.Should().Be(65);
        var ack = replayDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(DefaultServingRevisionCommittedAck.Descriptor))
            .Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<DefaultServingRevisionCommittedAck>();
        ack.OperationId.Should().Be("operation-1");
        ack.Disposition.Should().Be(DefaultServingRevisionCommitDisposition.Superseded);
        ack.SupersededByGeneration.Should().Be(65);
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldRejectConflictingOperationAtSameGeneration()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var committed = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r-first",
            OperationId = "operation-first",
            CommandId = "command-first",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-first",
            DeploymentId = "deployment-first",
            ServingGeneration = 5,
        };
        await agent.HandleSetDefaultServingRevisionAsync(committed);
        var committedVersion = agent.State.LastAppliedEventVersion;
        var conflicting = committed.Clone();
        conflicting.RevisionId = "r-conflict";
        conflicting.OperationId = "operation-conflict";
        conflicting.CommandId = "command-conflict";
        conflicting.DeploymentId = "deployment-conflict";

        var act = () => agent.HandleSetDefaultServingRevisionAsync(conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*generation '5' is already bound*");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.DefaultServingRevisionId.Should().Be("r-first");
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldRejectCurrentGenerationWithoutOperationRecord()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var committed = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r-first",
            OperationId = "operation-first",
            CommandId = "command-first",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-first",
            DeploymentId = "deployment-first",
            ServingGeneration = 5,
        };
        await agent.HandleSetDefaultServingRevisionAsync(committed);
        agent.State.DefaultServingRevisionOperations.Remove(committed.OperationId);
        var inconsistentVersion = agent.State.LastAppliedEventVersion;
        var replacement = committed.Clone();
        replacement.RevisionId = "r-replacement";
        replacement.OperationId = "operation-replacement";
        replacement.CommandId = "command-replacement";
        replacement.DeploymentId = "deployment-replacement";

        var act = () => agent.HandleSetDefaultServingRevisionAsync(replacement);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*generation '5' has no committed operation record*");
        agent.State.LastAppliedEventVersion.Should().Be(inconsistentVersion);
        agent.State.DefaultServingRevisionId.Should().Be("r-first");
    }

    [Fact]
    public async Task OrchestratedDefaultServingCommand_ShouldAcceptOnlyCanonicalDeploymentPublisher()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            actorId,
            () => new ServiceDefinitionGAgent(dispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var command = new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
            OperationId = "operation-default-r2",
            CommandId = "command-default-r2",
            ReplyActorId = ServiceActorIds.Deployment(identity),
            ActivationAttemptId = "attempt-r2",
            DeploymentId = "deployment-r2",
            ServingGeneration = 7,
        };

        await agent.HandleEventAsync(CreateDefaultServingCommandEnvelope(
            actorId,
            "foreign-deployment",
            command));

        agent.State.LastAppliedEventVersion.Should().Be(1);
        agent.State.DefaultServingRevisionId.Should().BeEmpty();

        await agent.HandleEventAsync(CreateDefaultServingCommandEnvelope(
            actorId,
            ServiceActorIds.Deployment(identity),
            command));

        agent.State.LastAppliedEventVersion.Should().Be(2);
        agent.State.DefaultServingRevisionId.Should().Be("r2");
    }

    [Fact]
    public async Task ExternalExposureRegistration_ShouldMovePendingToRegistered()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
        };
        var tokenAccessor = new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(dispatchPort, registrationPort, tokenAccessor));

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/api/services/svc/openapi.json",
            DesiredSpecHash = "hash-1",
            CredentialKid = "kid-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/api/services/svc/openapi.json",
        });

        agent.State.Spec.ExternalExposure.Should().NotBeNull();
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Registered);
        agent.State.Spec.ExternalExposure.NyxidServiceId.Should().Be("nyx-svc-1");
        agent.State.Spec.ExternalExposure.NyxidSlug.Should().Be("aevatar-orders");
        agent.State.Spec.ExternalExposure.DesiredSpecHash.Should().Be("hash-1");
        agent.State.Spec.ExternalExposure.RegisteredSpecHash.Should().Be("hash-1");
        agent.State.Spec.ExternalExposure.CredentialKid.Should().Be("kid-1");
        agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeTrue();
        registrationPort.RegisterRequests.Should().ContainSingle()
            .Which.AccessToken.Should().Be("owner-token");
        registrationPort.RegisterRequests.Single().ServiceCredential.Should().Be("scope-token:kid-1");
        dispatchPort.Calls.Should().Contain(x =>
            x.ActorId == ServiceActorIds.Definition(identity) &&
            x.Envelope.Payload.Is(RunRegistrationAttemptCommand.Descriptor));
    }

    [Fact]
    public async Task ReconcileExternalExposureAsync_ShouldNoOp_WhenRegisteredHashMatches()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
        };
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1")));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });
        var versionAfterSuccess = agent.State.LastAppliedEventVersion;

        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });

        agent.State.LastAppliedEventVersion.Should().Be(versionAfterSuccess);
        registrationPort.RegisterRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileExternalExposureAsync_ShouldUpdate_WhenHashDrifts()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
            UpdateResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-2"),
        };
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1")));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-2",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 2,
            DesiredSpecHash = "hash-2",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        registrationPort.RegisterRequests.Should().ContainSingle();
        registrationPort.UpdateRequests.Should().ContainSingle()
            .Which.ExistingNyxIdServiceId.Should().Be("nyx-svc-1");
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Registered);
        agent.State.Spec.ExternalExposure.DesiredSpecHash.Should().Be("hash-2");
        agent.State.Spec.ExternalExposure.RegisteredSpecHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task ReconcileExternalExposureAsync_ShouldUpdateCredential_WhenCredentialKidRotates()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
            UpdateResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
        };
        var tokenAccessor = new RotatingNyxIdRegistrationTokenAccessor("owner-token", "kid-1");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                tokenAccessor));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
            CredentialKid = "kid-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        tokenAccessor.CredentialKid = "kid-2";
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
            CredentialKid = "kid-2",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 2,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        registrationPort.RegisterRequests.Should().ContainSingle();
        registrationPort.UpdateRequests.Should().ContainSingle()
            .Which.ServiceCredential.Should().Be("scope-token:kid-2");
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Registered);
        agent.State.Spec.ExternalExposure.CredentialKid.Should().Be("kid-2");
    }

    [Fact]
    public async Task RunRegistrationAttemptAsync_ShouldRecoverAlreadyExistsThroughLookup()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
            UpdateResult = NyxIdServiceRegistrationResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Conflict, "exists", true),
                alreadyExists: true),
            LookupResult = NyxIdServiceLookupResult.Success("nyx-svc-1", "aevatar-orders", "hash-2"),
        };
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1")));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-2",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 2,
            DesiredSpecHash = "hash-2",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        registrationPort.LookupRequests.Should().ContainSingle()
            .Which.NyxIdServiceId.Should().Be("nyx-svc-1");
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Registered);
        agent.State.Spec.ExternalExposure.RegisteredSpecHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task RunRegistrationAttemptAsync_ShouldPersistMissingTokenFailure()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(dispatchPort, registrationPort, new StubNyxIdRegistrationTokenAccessor(null)));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });

        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        registrationPort.RegisterRequests.Should().BeEmpty();
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Failed);
        agent.State.Spec.ExternalExposure.LastError.Should().StartWith("MissingToken:");
        agent.State.Spec.ExternalExposure.NextAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunRegistrationAttemptAsync_ShouldStopRetry_WhenMaxAttemptsExhausted()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                dispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor(null),
                ServiceExternalExposureRetrySettings.Create(1, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10))));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });

        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Failed);
        agent.State.Spec.ExternalExposure.Attempt.Should().Be(1);
        agent.State.Spec.ExternalExposure.LastError.Should().StartWith("retry_exhausted:MissingToken:");
        agent.State.Spec.ExternalExposure.NextAttemptAt.Should().BeNull();
        dispatchPort.Calls.Count(call => call.Envelope.Payload.Is(RegistrationRetryDueCommand.Descriptor)).Should().Be(0);
    }

    [Fact]
    public async Task ReconcileExternalExposureAsync_ShouldRestartAtFirstAttempt_WhenPreviousAttemptWasExhausted()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor(null),
                ServiceExternalExposureRetrySettings.Create(1, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10))));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });
        agent.State.Spec.ExternalExposure.Attempt.Should().Be(1);
        agent.State.Spec.ExternalExposure.LastError.Should().StartWith("retry_exhausted:");

        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-2",
        });

        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Pending);
        agent.State.Spec.ExternalExposure.Attempt.Should().Be(1);
        agent.State.Spec.ExternalExposure.DesiredSpecHash.Should().Be("hash-2");
        agent.State.Spec.ExternalExposure.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task RegistrationRetryDueAsync_ShouldRejectStaleAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor(null)));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });
        var versionAfterFailure = agent.State.LastAppliedEventVersion;

        await agent.HandleRegistrationRetryDueAsync(new RegistrationRetryDueCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "old-hash",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        agent.State.LastAppliedEventVersion.Should().Be(versionAfterFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetireExternalExposureAsync_ShouldPersistOptOutWithoutNyxId_AndRejectStaleAttempts(
        bool failBeforeRetire)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1")));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
            CredentialKid = "kid-1",
        });

        if (failBeforeRetire)
        {
            await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
            {
                Identity = identity.Clone(),
                ExpectedAttempt = 1,
                DesiredSpecHash = "hash-1",
                OpenapiUrl = "https://api.test/openapi.json",
            });

            agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Failed);
            agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeTrue();
            agent.State.Spec.ExternalExposure.NyxidServiceId.Should().BeEmpty();
        }
        else
        {
            agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Pending);
            agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeTrue();
            agent.State.Spec.ExternalExposure.NyxidServiceId.Should().BeEmpty();
        }

        await agent.HandleRetireExternalExposureAsync(new RetireExternalExposureCommand
        {
            Identity = identity.Clone(),
        });

        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Retired);
        agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeFalse();
        agent.State.Spec.ExternalExposure.NyxidServiceId.Should().BeEmpty();
        agent.State.Spec.ExternalExposure.NextAttemptAt.Should().BeNull();
        registrationPort.RetireRequests.Should().BeEmpty();
        var versionAfterRetire = agent.State.LastAppliedEventVersion;
        var registerRequestCount = registrationPort.RegisterRequests.Count;

        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });
        await agent.HandleRegistrationRetryDueAsync(new RegistrationRetryDueCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        agent.State.LastAppliedEventVersion.Should().Be(versionAfterRetire);
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Retired);
        agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeFalse();
        registrationPort.RegisterRequests.Should().HaveCount(registerRequestCount);
        registrationPort.RetireRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task RetireExternalExposureAsync_ShouldMarkRegistrationRetired()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var registrationPort = new RecordingNyxIdServiceRegistrationPort
        {
            RegisterResult = NyxIdServiceRegistrationResult.Success("nyx-svc-1", "aevatar-orders", "hash-1"),
        };
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            () => new ServiceDefinitionGAgent(
                GAgentServiceTestKit.NoOpDispatchPort,
                registrationPort,
                new StubNyxIdRegistrationTokenAccessor("owner-token", "kid-1")));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        await agent.HandleReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/openapi.json",
            DesiredSpecHash = "hash-1",
        });
        await agent.HandleRunRegistrationAttemptAsync(new RunRegistrationAttemptCommand
        {
            Identity = identity.Clone(),
            ExpectedAttempt = 1,
            DesiredSpecHash = "hash-1",
            OpenapiUrl = "https://api.test/openapi.json",
        });

        await agent.HandleRetireExternalExposureAsync(new RetireExternalExposureCommand
        {
            Identity = identity.Clone(),
        });

        registrationPort.RetireRequests.Should().ContainSingle()
            .Which.NyxIdServiceId.Should().Be("nyx-svc-1");
        agent.State.Spec.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Retired);
        agent.State.Spec.ExternalExposure.ExposureDesired.Should().BeFalse();
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldRejectMissingDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));

        var act = () => agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldRejectMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity(serviceId: "svc-other");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(otherIdentity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    [Fact]
    public async Task HandleSetDefaultServingRevisionAsync_ShouldRejectBlankRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = " ",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("revision_id is required.");
    }

    [Fact]
    public async Task HandleSetDefaultServingRevisionAsync_ShouldRejectUncoordinatedCommand()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var committedVersion = agent.State.LastAppliedEventVersion;

        var act = () => agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("operation_id is required for default-serving coordination.");
        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.DefaultServingRevisionId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectSpecWithoutIdentity()
    {
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            "service-definition:missing-identity",
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                DisplayName = "Service",
                Endpoints =
                {
                    GAgentServiceTestKit.CreateEndpointSpec(),
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service identity is required.");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectSpecWithoutEndpoints()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent(GAgentServiceTestKit.NoOpDispatchPort));

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                Identity = identity.Clone(),
                DisplayName = "Service",
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service endpoints are required.");
    }

    private static EventEnvelope CreateDefaultServingCommandEnvelope(
        string subscriberActorId,
        string publisherActorId,
        SetDefaultServingRevisionCommand command) =>
        new()
        {
            Id = command.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, subscriberActorId),
            Propagation = new EnvelopePropagation(),
        };

    private sealed class SelectiveFailingActorDispatchPort : IActorDispatchPort
    {
        public bool FailInvocationCatalogObservations { get; set; }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            if (FailInvocationCatalogObservations &&
                envelope.Payload.Is(ObserveServiceInvocationCatalogCommand.Descriptor))
            {
                throw new InvalidOperationException("invocation catalog observation dispatch failed");
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StubNyxIdRegistrationTokenAccessor : INyxIdRegistrationTokenAccessor
    {
        private readonly NyxIdRegistrationToken? _token;

        public StubNyxIdRegistrationTokenAccessor(string? accessToken, string credentialKid = "")
        {
            _token = string.IsNullOrWhiteSpace(accessToken)
                ? null
                : new NyxIdRegistrationToken(accessToken, $"scope-token:{credentialKid}", credentialKid);
        }

        public Task<NyxIdRegistrationToken?> GetTokenAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult(_token);
    }

    private sealed class RotatingNyxIdRegistrationTokenAccessor(
        string ownerAccessToken,
        string credentialKid) : INyxIdRegistrationTokenAccessor
    {
        public string CredentialKid { get; set; } = credentialKid;

        public Task<NyxIdRegistrationToken?> GetTokenAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<NyxIdRegistrationToken?>(
                new NyxIdRegistrationToken(ownerAccessToken, $"scope-token:{CredentialKid}", CredentialKid));
    }

    private sealed class RecordingNyxIdServiceRegistrationPort : INyxIdServiceRegistrationPort
    {
        public NyxIdServiceRegistrationResult RegisterResult { get; init; } =
            NyxIdServiceRegistrationResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Transient, "not-configured", true));

        public NyxIdServiceRegistrationResult UpdateResult { get; init; } =
            NyxIdServiceRegistrationResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Transient, "not-configured", true));

        public NyxIdServiceLookupResult LookupResult { get; init; } =
            NyxIdServiceLookupResult.Missing();

        public NyxIdServiceRetirementResult RetireResult { get; init; } =
            NyxIdServiceRetirementResult.Success();

        public List<NyxIdServiceRegistrationRequest> RegisterRequests { get; } = [];

        public List<NyxIdServiceRegistrationRequest> UpdateRequests { get; } = [];

        public List<NyxIdServiceLookupRequest> LookupRequests { get; } = [];

        public List<NyxIdServiceRetirementRequest> RetireRequests { get; } = [];

        public Task<NyxIdServiceRegistrationResult> RegisterAsync(
            NyxIdServiceRegistrationRequest request,
            CancellationToken ct = default)
        {
            RegisterRequests.Add(request);
            return Task.FromResult(RegisterResult);
        }

        public Task<NyxIdServiceRegistrationResult> UpdateAsync(
            NyxIdServiceRegistrationRequest request,
            CancellationToken ct = default)
        {
            UpdateRequests.Add(request);
            return Task.FromResult(UpdateResult);
        }

        public Task<NyxIdServiceLookupResult> GetAsync(
            NyxIdServiceLookupRequest request,
            CancellationToken ct = default)
        {
            LookupRequests.Add(request);
            return Task.FromResult(LookupResult);
        }

        public Task<NyxIdServiceRetirementResult> RetireAsync(
            NyxIdServiceRetirementRequest request,
            CancellationToken ct = default)
        {
            RetireRequests.Add(request);
            return Task.FromResult(RetireResult);
        }
    }
}
