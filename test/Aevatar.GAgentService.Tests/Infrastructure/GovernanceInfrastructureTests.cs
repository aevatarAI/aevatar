using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Infrastructure.Activation;
using Aevatar.GAgentService.Governance.Infrastructure.Admission;
using Aevatar.GAgentService.Governance.Infrastructure.Migration;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class GovernanceInfrastructureTests
{
    [Fact]
    public async Task DefaultActivationAdmissionEvaluator_ShouldFlagMissingPoliciesAndBindings()
    {
        var evaluator = new DefaultActivationAdmissionEvaluator();
        var request = new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                MissingPolicyIds = { "policy-missing" },
                Bindings =
                {
                    new ServiceBindingSpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        BindingId = "binding-a",
                        BindingKind = ServiceBindingKind.Service,
                        ServiceRef = new BoundServiceRef
                        {
                            Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                            EndpointId = "run",
                        },
                    },
                },
                Policies =
                {
                    new ServicePolicySpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        PolicyId = "policy-a",
                        ActivationRequiredBindingIds = { "binding-missing" },
                    },
                },
            },
        };

        var decision = await evaluator.EvaluateAsync(request);

        decision.Allowed.Should().BeFalse();
        decision.Violations.Select(x => x.Code).Should().BeEquivalentTo(["missing_policy", "missing_binding"]);
    }

    [Fact]
    public async Task DefaultActivationAdmissionEvaluator_ShouldHonorCancellationAndAllowSatisfiedRequests()
    {
        var evaluator = new DefaultActivationAdmissionEvaluator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceled = () => evaluator.EvaluateAsync(new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView(),
        }, cts.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        var allowed = await evaluator.EvaluateAsync(new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView
            {
                Bindings =
                {
                    new ServiceBindingSpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        BindingId = "binding-a",
                        BindingKind = ServiceBindingKind.Secret,
                        SecretRef = new BoundSecretRef
                        {
                            SecretName = "secret",
                        },
                    },
                },
                Policies =
                {
                    new ServicePolicySpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        PolicyId = "policy-a",
                        ActivationRequiredBindingIds = { "binding-a" },
                    },
                },
            },
        });

        allowed.Allowed.Should().BeTrue();
        allowed.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultInvokeAdmissionEvaluator_ShouldEnforceExposurePolicyAndCallerRules()
    {
        var evaluator = new DefaultInvokeAdmissionEvaluator();
        var request = new InvokeAdmissionRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            ServiceKey = "tenant/app/default/svc",
            EndpointId = "invoke",
            Endpoint = new ServiceEndpointExposureSpec
            {
                EndpointId = "invoke",
                ExposureKind = ServiceEndpointExposureKind.Disabled,
            },
            MissingPolicyIds = { "policy-missing" },
            HasActiveDeployment = false,
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = "tenant/app/default/caller",
            },
            Policies =
            {
                new ServicePolicySpec
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(),
                    PolicyId = "policy-a",
                    InvokeRequiresActiveDeployment = true,
                    InvokeAllowedCallerServiceKeys = { "tenant/app/default/allowed" },
                },
            },
        };

        var denied = await evaluator.EvaluateAsync(request);

        denied.Allowed.Should().BeFalse();
        denied.Violations.Select(x => x.Code).Should().BeEquivalentTo([
            "missing_policy",
            "endpoint_disabled",
            "inactive_deployment",
            "caller_not_allowed",
        ]);

        var allowed = await evaluator.EvaluateAsync(new InvokeAdmissionRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            ServiceKey = "tenant/app/default/svc",
            EndpointId = "invoke",
            Endpoint = new ServiceEndpointExposureSpec
            {
                EndpointId = "invoke",
                ExposureKind = ServiceEndpointExposureKind.Public,
            },
            HasActiveDeployment = true,
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = "tenant/app/default/allowed",
            },
            Policies =
            {
                new ServicePolicySpec
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(),
                    PolicyId = "policy-a",
                    InvokeRequiresActiveDeployment = true,
                    InvokeAllowedCallerServiceKeys = { "tenant/app/default/allowed" },
                },
            },
        });

        allowed.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task DefaultServiceGovernanceCommandTargetProvisioner_ShouldCreateAndReuseGovernanceActors()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var importer = new RecordingLegacyImporter();
        var provisioner = new DefaultServiceGovernanceCommandTargetProvisioner(runtime, importer);

        var configurationTarget = await provisioner.EnsureConfigurationTargetAsync(identity);

        configurationTarget.Should().Be(ServiceActorIds.Configuration(identity));
        importer.Requests.Should().ContainSingle(x => x.ServiceId == identity.ServiceId);
        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be((typeof(ServiceConfigurationGAgent), ServiceActorIds.Configuration(identity)));

        runtime.MarkExisting(ServiceActorIds.Configuration(identity));
        runtime.CreateCalls.Clear();

        await provisioner.EnsureConfigurationTargetAsync(identity);

        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultServiceGovernanceCommandTargetProvisioner_ShouldValidateInputs()
    {
        var runtime = new RecordingActorRuntime();
        var importer = new RecordingLegacyImporter();
        Action nullImporter = () => new DefaultServiceGovernanceCommandTargetProvisioner(runtime, null!);
        var provisioner = new DefaultServiceGovernanceCommandTargetProvisioner(runtime, importer);
        var act = () => provisioner.EnsureConfigurationTargetAsync(null!);

        nullImporter.Should().Throw<ArgumentNullException>();
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ActorTargetProvisionerBase_ShouldValidateActorId_AndRejectMissingActorAfterExistenceCheck()
    {
        var runtime = new RecordingActorRuntime();
        var provisioner = new TestProvisioner(runtime);
        var blank = () => provisioner.EnsureAsync<TestStaticServiceAgent>(" ");

        await blank.Should().ThrowAsync<ArgumentException>();

        runtime.MarkExistingWithoutActor("actor-missing");
        var missing = () => provisioner.EnsureAsync<TestStaticServiceAgent>("actor-missing");

        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found after existence check*");
    }

    [Fact]
    public async Task DefaultServiceGovernanceLegacyImporter_ShouldImportLegacyGovernanceStreams()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        await AppendLegacyGovernanceEventsAsync(eventStore, identity);
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var importer = new DefaultServiceGovernanceLegacyImporter(eventStore, runtime, dispatchPort, projectionPort);

        var imported = await importer.ImportIfNeededAsync(identity);

        imported.Should().BeTrue();
        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be((typeof(ServiceConfigurationGAgent), ServiceActorIds.Configuration(identity)));
        projectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Configuration(identity));
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be(ServiceActorIds.Configuration(identity));
        dispatchPort.Calls[0].command.State.Bindings.Should().ContainKey("binding-a");
        dispatchPort.Calls[0].command.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke");
        dispatchPort.Calls[0].command.State.Policies.Should().ContainKey("policy-a");
    }

    [Fact]
    public async Task DefaultServiceGovernanceLegacyImporter_ShouldSkipWhenConfigurationAlreadyExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        await AppendLegacyGovernanceEventsAsync(eventStore, identity);
        await eventStore.AppendAsync(
            ServiceActorIds.Configuration(identity),
            [
                new StateEvent
                {
                    AgentId = ServiceActorIds.Configuration(identity),
                    EventId = "evt-1",
                    EventType = LegacyServiceConfigurationImportedEvent.Descriptor.FullName,
                    Version = 1,
                    EventData = Any.Pack(new LegacyServiceConfigurationImportedEvent
                    {
                        State = new ServiceConfigurationState
                        {
                            Identity = identity.Clone(),
                        },
                    }),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                },
            ],
            expectedVersion: 0);

        var importer = new DefaultServiceGovernanceLegacyImporter(
            eventStore,
            new RecordingActorRuntime(),
            new RecordingDispatchPort(),
            new RecordingConfigurationProjectionPort());

        var imported = await importer.ImportIfNeededAsync(identity);

        imported.Should().BeFalse();
    }

    [Fact]
    public async Task DefaultServiceGovernanceLegacyImporter_ShouldSkipWhenNoLegacyStateExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var importer = new DefaultServiceGovernanceLegacyImporter(
            new InMemoryEventStore(),
            runtime,
            dispatchPort,
            projectionPort);

        var imported = await importer.ImportIfNeededAsync(identity);

        imported.Should().BeFalse();
        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
        projectionPort.ActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultServiceGovernanceLegacyImporter_ShouldReuseExistingConfigurationActor()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        await AppendLegacyGovernanceEventsAsync(eventStore, identity);
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Configuration(identity));
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var importer = new DefaultServiceGovernanceLegacyImporter(eventStore, runtime, dispatchPort, projectionPort);

        var imported = await importer.ImportIfNeededAsync(identity);

        imported.Should().BeTrue();
        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().ContainSingle();
        projectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Configuration(identity));
    }

    [Fact]
    public async Task DefaultServiceGovernanceLegacyImporter_ShouldFoldUpdatedAndRetiredLegacyRecords()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        await eventStore.AppendAsync(
            ServiceActorIds.Bindings(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.Bindings(identity),
                    1,
                    new ServiceBindingCreatedEvent
                    {
                        Spec = new ServiceBindingSpec
                        {
                            Identity = identity.Clone(),
                            BindingId = "binding-a",
                            DisplayName = "Binding A",
                            BindingKind = ServiceBindingKind.Service,
                            ServiceRef = new BoundServiceRef
                            {
                                Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                                EndpointId = "run",
                            },
                        },
                    }),
                BuildStateEvent(
                    ServiceActorIds.Bindings(identity),
                    2,
                    new ServiceBindingRetiredEvent
                    {
                        Identity = identity.Clone(),
                        BindingId = "binding-a",
                    }),
            ],
            expectedVersion: 0);
        await eventStore.AppendAsync(
            ServiceActorIds.EndpointCatalog(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.EndpointCatalog(identity),
                    1,
                    new ServiceEndpointCatalogCreatedEvent
                    {
                        Spec = new ServiceEndpointCatalogSpec
                        {
                            Identity = identity.Clone(),
                            Endpoints =
                            {
                                new ServiceEndpointExposureSpec
                                {
                                    EndpointId = "invoke",
                                    DisplayName = "Invoke",
                                    Kind = ServiceEndpointKind.Command,
                                    RequestTypeUrl = "type.googleapis.com/demo.Invoke",
                                    ExposureKind = ServiceEndpointExposureKind.Internal,
                                },
                            },
                        },
                    }),
                BuildStateEvent(
                    ServiceActorIds.EndpointCatalog(identity),
                    2,
                    new ServiceEndpointCatalogUpdatedEvent
                    {
                        Spec = new ServiceEndpointCatalogSpec
                        {
                            Identity = identity.Clone(),
                            Endpoints =
                            {
                                new ServiceEndpointExposureSpec
                                {
                                    EndpointId = "chat",
                                    DisplayName = "Chat",
                                    Kind = ServiceEndpointKind.Chat,
                                    RequestTypeUrl = "type.googleapis.com/demo.Chat",
                                    ResponseTypeUrl = "type.googleapis.com/demo.ChatResult",
                                    ExposureKind = ServiceEndpointExposureKind.Public,
                                },
                            },
                        },
                    }),
            ],
            expectedVersion: 0);
        await eventStore.AppendAsync(
            ServiceActorIds.Policies(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.Policies(identity),
                    1,
                    new ServicePolicyCreatedEvent
                    {
                        Spec = new ServicePolicySpec
                        {
                            Identity = identity.Clone(),
                            PolicyId = "policy-a",
                            DisplayName = "Policy A",
                        },
                    }),
                BuildStateEvent(
                    ServiceActorIds.Policies(identity),
                    2,
                    new ServicePolicyRetiredEvent
                    {
                        Identity = identity.Clone(),
                        PolicyId = "policy-a",
                    }),
            ],
            expectedVersion: 0);

        var dispatchPort = new RecordingDispatchPort();
        var importer = new DefaultServiceGovernanceLegacyImporter(
            eventStore,
            new RecordingActorRuntime(),
            dispatchPort,
            new RecordingConfigurationProjectionPort());

        var imported = await importer.ImportIfNeededAsync(identity);

        imported.Should().BeTrue();
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].command.State.Bindings["binding-a"].Retired.Should().BeTrue();
        dispatchPort.Calls[0].command.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        dispatchPort.Calls[0].command.State.Policies["policy-a"].Retired.Should().BeTrue();
    }

    private static async Task AppendLegacyGovernanceEventsAsync(IEventStore eventStore, ServiceIdentity identity)
    {
        await eventStore.AppendAsync(
            ServiceActorIds.Bindings(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.Bindings(identity),
                    1,
                    new ServiceBindingCreatedEvent
                    {
                        Spec = new ServiceBindingSpec
                        {
                            Identity = identity.Clone(),
                            BindingId = "binding-a",
                            DisplayName = "Binding A",
                            BindingKind = ServiceBindingKind.Service,
                            ServiceRef = new BoundServiceRef
                            {
                                Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                                EndpointId = "run",
                            },
                        },
                    }),
            ],
            expectedVersion: 0);

        await eventStore.AppendAsync(
            ServiceActorIds.EndpointCatalog(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.EndpointCatalog(identity),
                    1,
                    new ServiceEndpointCatalogCreatedEvent
                    {
                        Spec = new ServiceEndpointCatalogSpec
                        {
                            Identity = identity.Clone(),
                            Endpoints =
                            {
                                new ServiceEndpointExposureSpec
                                {
                                    EndpointId = "invoke",
                                    DisplayName = "Invoke",
                                    Kind = ServiceEndpointKind.Command,
                                    RequestTypeUrl = "type.googleapis.com/demo.Invoke",
                                    ExposureKind = ServiceEndpointExposureKind.Public,
                                },
                            },
                        },
                    }),
            ],
            expectedVersion: 0);

        await eventStore.AppendAsync(
            ServiceActorIds.Policies(identity),
            [
                BuildStateEvent(
                    ServiceActorIds.Policies(identity),
                    1,
                    new ServicePolicyCreatedEvent
                    {
                        Spec = new ServicePolicySpec
                        {
                            Identity = identity.Clone(),
                            PolicyId = "policy-a",
                            DisplayName = "Policy A",
                            ActivationRequiredBindingIds = { "binding-a" },
                        },
                    }),
            ],
            expectedVersion: 0);
    }

    private static StateEvent BuildStateEvent(string agentId, long version, Google.Protobuf.IMessage payload)
    {
        return new StateEvent
        {
            AgentId = agentId,
            EventId = $"{agentId}:{version}",
            EventType = payload.Descriptor.FullName,
            Version = version,
            EventData = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _existingWithoutActor = new(StringComparer.Ordinal);

        public List<(System.Type actorType, string actorId)> CreateCalls { get; } = [];

        public void MarkExisting(string actorId)
        {
            _actors[actorId] = new RecordingActor(actorId);
        }

        public void MarkExistingWithoutActor(string actorId)
        {
            _existingWithoutActor.Add(actorId);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id) || _existingWithoutActor.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingLegacyImporter : IServiceGovernanceLegacyImporter
    {
        public List<ServiceIdentity> Requests { get; } = [];

        public Task<bool> ImportIfNeededAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Requests.Add(identity.Clone());
            return Task.FromResult(false);
        }
    }

    private sealed class TestProvisioner : ActorTargetProvisionerBase
    {
        public TestProvisioner(IActorRuntime runtime)
            : base(runtime)
        {
        }

        public Task<string> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent =>
            EnsureActorAsync<TAgent>(actorId, ct);
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, ImportLegacyServiceConfigurationCommand command)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Payload.Unpack<ImportLegacyServiceConfigurationCommand>()));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConfigurationProjectionPort : IServiceConfigurationProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
