using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Governance.Hosting.Migration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceGovernanceLegacyMigrationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldImportLegacyGovernanceStreams()
    {
        var identity = CreateIdentity("service-a");
        var eventStore = new InMemoryEventStore();
        await AppendLegacyGovernanceEventsAsync(eventStore, identity);
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
        };
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader(services),
            eventStore,
            runtime,
            dispatchPort,
            projectionPort,
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

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
    public async Task StartAsync_ShouldSkipWhenConfigurationAlreadyExists()
    {
        var identity = CreateIdentity("service-a");
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

        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
        };
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader(services),
            eventStore,
            runtime,
            dispatchPort,
            new RecordingConfigurationProjectionPort(),
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldSkipWhenNoLegacyStateExists()
    {
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
        };
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader(services),
            new InMemoryEventStore(),
            runtime,
            dispatchPort,
            projectionPort,
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
        projectionPort.ActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldReuseExistingConfigurationActor()
    {
        var identity = CreateIdentity("service-a");
        var eventStore = new InMemoryEventStore();
        await AppendLegacyGovernanceEventsAsync(eventStore, identity);
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Configuration(identity));
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingConfigurationProjectionPort();
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
        };
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader(services),
            eventStore,
            runtime,
            dispatchPort,
            projectionPort,
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().ContainSingle();
        projectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Configuration(identity));
    }

    [Fact]
    public async Task StartAsync_ShouldFoldUpdatedAndRetiredLegacyRecords()
    {
        var identity = CreateIdentity("service-a");
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
                                Identity = CreateIdentity("dependency"),
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
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
        };
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader(services),
            eventStore,
            new RecordingActorRuntime(),
            dispatchPort,
            new RecordingConfigurationProjectionPort(),
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].command.State.Bindings["binding-a"].Retired.Should().BeTrue();
        dispatchPort.Calls[0].command.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        dispatchPort.Calls[0].command.State.Policies["policy-a"].Retired.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldComplete()
    {
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader([]),
            new InMemoryEventStore(),
            new RecordingActorRuntime(),
            new RecordingDispatchPort(),
            new RecordingConfigurationProjectionPort(),
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.Invoking(x => x.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    private static ServiceIdentity CreateIdentity(string serviceId) => new()
    {
        TenantId = "tenant-a",
        AppId = "app-a",
        Namespace = "default",
        ServiceId = serviceId,
    };

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
                                Identity = CreateIdentity("dependency"),
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

    private sealed class StubServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        private readonly IReadOnlyList<ServiceCatalogSnapshot> _services;

        public StubServiceCatalogQueryReader(IReadOnlyList<ServiceCatalogSnapshot> services)
        {
            _services = services;
        }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var key = $"{identity.TenantId}/{identity.AppId}/{identity.Namespace}/{identity.ServiceId}";
            return Task.FromResult(_services.FirstOrDefault(x => string.Equals(x.ServiceKey, key, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            IReadOnlyList<ServiceCatalogSnapshot> filtered = _services
                .Where(x =>
                    string.Equals(x.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(x.AppId, appId, StringComparison.Ordinal) &&
                    string.Equals(x.Namespace, @namespace, StringComparison.Ordinal))
                .Take(take)
                .ToList();
            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceCatalogSnapshot> filtered = _services.Take(take).ToList();
            return Task.FromResult(filtered);
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public List<(System.Type actorType, string actorId)> CreateCalls { get; } = [];

        public void MarkExisting(string actorId)
        {
            _actors[actorId] = new RecordingActor(actorId);
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
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
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

        public IAgent Agent { get; } = null!;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
