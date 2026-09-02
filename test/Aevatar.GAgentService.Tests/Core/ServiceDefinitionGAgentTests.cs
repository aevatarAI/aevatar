using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;

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
    public async Task HandleUpdateAndSetDefaultServingRevisionAsync_ShouldMutateExistingDefinition()
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
        await agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        agent.State.Spec.DisplayName.Should().Be("Updated");
        agent.State.Spec.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        agent.State.DefaultServingRevisionId.Should().Be("r2");
        agent.State.LastAppliedEventVersion.Should().Be(3);
        dispatchPort.Calls.Should().HaveCount(3);
        dispatchPort.Calls.Should().OnlyContain(x =>
            x.ActorId == ServiceActorIds.InvocationCatalog(identity));
        var updateObservation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationCatalogCommand>();
        updateObservation.SourceCatalogVersion.Should().Be(2);
        updateObservation.Identity.Should().BeEquivalentTo(identity);
        updateObservation.ServiceEndpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        updateObservation.ServiceEndpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);
        updateObservation.ServiceEndpoints[0].RequestTypeUrl.Should().Be("type.googleapis.com/test.chat");
        var defaultObservation = dispatchPort.Calls[2].Envelope.Payload.Unpack<ObserveServiceInvocationCatalogCommand>();
        defaultObservation.SourceCatalogVersion.Should().Be(3);
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
