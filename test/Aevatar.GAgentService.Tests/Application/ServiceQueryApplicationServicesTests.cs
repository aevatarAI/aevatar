using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceQueryApplicationServicesTests
{
    [Fact]
    public async Task LifecycleQueryService_ShouldDelegateToCatalogRevisionAndDeploymentReaders()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalogReader = new RecordingCatalogReader();
        var revisionReader = new RecordingRevisionReader();
        var deploymentReader = new RecordingDeploymentReader();
        var service = new ServiceLifecycleQueryApplicationService(catalogReader, revisionReader, deploymentReader);

        _ = await service.GetServiceAsync(identity);
        _ = await service.GetServiceRevisionsAsync(identity);
        _ = await service.GetServiceDeploymentsAsync(identity);

        catalogReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        revisionReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        deploymentReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task ServingQueryService_ShouldDelegateToServingRolloutAndTrafficReaders()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var servingReader = new RecordingServingReader();
        var rolloutReader = new RecordingRolloutReader();
        var observationReader = new RecordingRolloutCommandObservationReader();
        var trafficReader = new RecordingTrafficReader();
        var service = new ServiceServingQueryApplicationService(servingReader, rolloutReader, observationReader, trafficReader);

        _ = await service.GetServiceServingSetAsync(identity);
        _ = await service.GetServiceRolloutAsync(identity);
        _ = await service.GetServiceRolloutCommandObservationAsync(identity, "cmd-1");
        _ = await service.GetServiceTrafficViewAsync(identity);

        servingReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        rolloutReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        observationReader.CommandIds.Should().ContainSingle().Which.Should().Be("cmd-1");
        trafficReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task ListServicesAsync_ShouldDelegateToCatalogReader()
    {
        var catalogReader = new RecordingCatalogReader();
        var service = new ServiceLifecycleQueryApplicationService(
            catalogReader,
            new RecordingRevisionReader(),
            new RecordingDeploymentReader());

        _ = await service.ListServicesAsync("tenant", "app", "ns", take: 42);

        catalogReader.ListCalls.Should().ContainSingle();
        catalogReader.ListCalls[0].Should().Be(("tenant", "app", "ns", 42));
    }

    [Fact]
    public async Task ListServicesAsync_ShouldReturnCatalogSnapshotsWithoutQueryTimeDeploymentSelection()
    {
        var serviceA = CreateServiceCatalogSnapshot("svc-a");
        var serviceB = CreateServiceCatalogSnapshot("svc-b");
        var catalogReader = new ConfiguredCatalogReader
        {
            QueryByScopeResult = [serviceA, serviceB],
        };
        var deploymentReader = new ConfiguredDeploymentReader
        {
            Results =
            {
                [serviceA.ServiceKey] = new ServiceDeploymentCatalogSnapshot(
                    serviceA.ServiceKey,
                    [
                        new ServiceDeploymentSnapshot(
                            "dep-old",
                            "r-old",
                            "actor-old",
                            ServiceDeploymentStatus.Active.ToString(),
                            DateTimeOffset.Parse("2026-03-14T00:10:00+00:00"),
                            DateTimeOffset.Parse("2026-03-14T00:11:00+00:00")),
                        new ServiceDeploymentSnapshot(
                            "dep-active",
                            "r-active",
                            "actor-active",
                            ServiceDeploymentStatus.Active.ToString(),
                            DateTimeOffset.Parse("2026-03-14T00:20:00+00:00"),
                            DateTimeOffset.Parse("2026-03-14T00:21:00+00:00")),
                    ],
                    DateTimeOffset.Parse("2026-03-14T00:21:00+00:00")),
                [serviceB.ServiceKey] = new ServiceDeploymentCatalogSnapshot(
                    serviceB.ServiceKey,
                    [
                        new ServiceDeploymentSnapshot(
                            "dep-inactive",
                            "r-inactive",
                            "actor-inactive",
                            ServiceDeploymentStatus.Deactivated.ToString(),
                            DateTimeOffset.Parse("2026-03-14T00:30:00+00:00"),
                            DateTimeOffset.Parse("2026-03-14T00:31:00+00:00")),
                    ],
                    DateTimeOffset.Parse("2026-03-14T00:31:00+00:00")),
            },
        };
        var service = new ServiceLifecycleQueryApplicationService(
            catalogReader,
            new RecordingRevisionReader(),
            deploymentReader);

        var result = await service.ListServicesAsync("tenant", "app", "default", take: 10);

        result.Should().Equal(serviceA, serviceB);
        result.Select(x => x.ActiveServingRevisionId).Should().OnlyContain(x => string.IsNullOrEmpty(x));
        deploymentReader.Identities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetServiceAsync_ShouldReturnNull_WhenCatalogReaderReturnsNull()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalogReader = new RecordingCatalogReader();
        var service = new ServiceLifecycleQueryApplicationService(
            catalogReader,
            new RecordingRevisionReader(),
            new RecordingDeploymentReader());

        var result = await service.GetServiceAsync(identity);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceAsync_ShouldReturnCatalogSnapshotWithoutQueryTimeDeploymentSelection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalogSnapshot = CreateServiceCatalogSnapshot();
        var catalogReader = new ConfiguredCatalogReader { GetResult = catalogSnapshot };
        var deploymentReader = new ConfiguredDeploymentReader
        {
            GetResult = new ServiceDeploymentCatalogSnapshot(
                "tenant:app:default:svc",
                [
                    new ServiceDeploymentSnapshot(
                        "dep-old",
                        "r-old",
                        "actor-old",
                        ServiceDeploymentStatus.Deactivated.ToString(),
                        DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"),
                        DateTimeOffset.Parse("2026-03-14T00:05:00+00:00")),
                    new ServiceDeploymentSnapshot(
                        "dep-active",
                        "r-active",
                        "actor-active",
                        ServiceDeploymentStatus.Active.ToString(),
                        DateTimeOffset.Parse("2026-03-14T00:10:00+00:00"),
                        DateTimeOffset.Parse("2026-03-14T00:15:00+00:00")),
                ],
                DateTimeOffset.Parse("2026-03-14T00:15:00+00:00")),
        };
        var service = new ServiceLifecycleQueryApplicationService(
            catalogReader,
            new RecordingRevisionReader(),
            deploymentReader);

        var result = await service.GetServiceAsync(identity);

        result.Should().Be(catalogSnapshot);
        result!.ActiveServingRevisionId.Should().BeEmpty();
        result.DeploymentId.Should().BeEmpty();
        result.PrimaryActorId.Should().BeEmpty();
        result.DeploymentStatus.Should().BeEmpty();
        deploymentReader.Identities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetServiceRevisionsAsync_ShouldReturnSnapshot_WhenReaderHasData()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionReader = new ConfiguredRevisionReader
        {
            GetResult = new ServiceRevisionCatalogSnapshot(
                "tenant:app:default:svc",
                [
                    new ServiceRevisionSnapshot(
                        "r1",
                        "Static",
                        "Published",
                        "abc123",
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow,
                        null,
                        DateTimeOffset.UtcNow,
                        null),
                ],
                DateTimeOffset.UtcNow),
        };
        var service = new ServiceLifecycleQueryApplicationService(
            new RecordingCatalogReader(),
            revisionReader,
            new RecordingDeploymentReader());

        var result = await service.GetServiceRevisionsAsync(identity);

        result.Should().NotBeNull();
        result!.Revisions.Should().ContainSingle();
        result.Revisions[0].RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task GetServiceDeploymentsAsync_ShouldReturnSnapshot_WhenReaderHasData()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var deploymentReader = new ConfiguredDeploymentReader
        {
            GetResult = new ServiceDeploymentCatalogSnapshot(
                "tenant:app:default:svc",
                [
                    new ServiceDeploymentSnapshot(
                        "dep-1",
                        "r1",
                        "actor-1",
                        "Active",
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                ],
                DateTimeOffset.UtcNow),
        };
        var service = new ServiceLifecycleQueryApplicationService(
            new RecordingCatalogReader(),
            new RecordingRevisionReader(),
            deploymentReader);

        var result = await service.GetServiceDeploymentsAsync(identity);

        result.Should().NotBeNull();
        result!.Deployments.Should().ContainSingle();
        result.Deployments[0].DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public async Task ServingQueryService_ShouldReturnNull_WhenNoData()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceServingQueryApplicationService(
            new RecordingServingReader(),
            new RecordingRolloutReader(),
            new RecordingRolloutCommandObservationReader(),
            new RecordingTrafficReader());

        var servingSet = await service.GetServiceServingSetAsync(identity);
        var rollout = await service.GetServiceRolloutAsync(identity);
        var observation = await service.GetServiceRolloutCommandObservationAsync(identity, "cmd-1");
        var trafficView = await service.GetServiceTrafficViewAsync(identity);

        servingSet.Should().BeNull();
        rollout.Should().BeNull();
        observation.Should().BeNull();
        trafficView.Should().BeNull();
    }

    [Fact]
    public async Task ServingQueryService_ShouldFilterObservationByServiceKey()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var observationReader = new RecordingRolloutCommandObservationReader
        {
            Snapshot = new ServiceRolloutCommandObservationSnapshot(
                "cmd-1",
                "corr-1",
                "other/app/ns/service",
                "rollout-1",
                ServiceRolloutStatus.Paused,
                true,
                42,
                DateTimeOffset.UtcNow),
        };
        var service = new ServiceServingQueryApplicationService(
            new RecordingServingReader(),
            new RecordingRolloutReader(),
            observationReader,
            new RecordingTrafficReader());

        var observation = await service.GetServiceRolloutCommandObservationAsync(identity, "cmd-1");

        observation.Should().BeNull();
    }

    private sealed class ConfiguredRevisionReader : IServiceRevisionCatalogQueryReader
    {
        public ServiceRevisionCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class ConfiguredCatalogReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }
        public IReadOnlyList<ServiceCatalogSnapshot> QueryByScopeResult { get; init; } = [];

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult(QueryByScopeResult);
    }

    private sealed class ConfiguredDeploymentReader : IServiceDeploymentCatalogQueryReader
    {
        public ServiceDeploymentCatalogSnapshot? GetResult { get; init; }
        public Dictionary<string, ServiceDeploymentCatalogSnapshot> Results { get; } = [];

        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceDeploymentCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult(Results.TryGetValue(ServiceKeys.Build(identity), out var result)
                ? result
                : GetResult);
        }
    }

    private sealed class RecordingCatalogReader : IServiceCatalogQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];
        public List<(string tenantId, string appId, string @namespace, int take)> ListCalls { get; } = [];

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceCatalogSnapshot?>(null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            ListCalls.Add((tenantId, appId, @namespace, take));
            return Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
        }
    }

    private sealed class RecordingRevisionReader : IServiceRevisionCatalogQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
        }
    }

    private sealed class RecordingDeploymentReader : IServiceDeploymentCatalogQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceDeploymentCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
        }
    }

    private sealed class RecordingServingReader : IServiceServingSetQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceServingSetSnapshot?>(null);
        }
    }

    private sealed class RecordingRolloutReader : IServiceRolloutQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceRolloutSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceRolloutSnapshot?>(null);
        }
    }

    private sealed class RecordingTrafficReader : IServiceTrafficViewQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceTrafficViewSnapshot?>(null);
        }
    }

    private sealed class RecordingRolloutCommandObservationReader : IServiceRolloutCommandObservationQueryReader
    {
        public List<string> CommandIds { get; } = [];

        public ServiceRolloutCommandObservationSnapshot? Snapshot { get; init; }

        public Task<ServiceRolloutCommandObservationSnapshot?> GetAsync(string commandId, CancellationToken ct = default)
        {
            CommandIds.Add(commandId);
            return Task.FromResult(Snapshot);
        }
    }

    private static ServiceCatalogSnapshot CreateServiceCatalogSnapshot(string serviceId = "svc") =>
        new(
            ServiceKey: $"tenant:app:default:{serviceId}",
            TenantId: "tenant",
            AppId: "app",
            Namespace: "default",
            ServiceId: serviceId,
            DisplayName: "Service",
            DefaultServingRevisionId: "r-default",
            ActiveServingRevisionId: string.Empty,
            DeploymentId: string.Empty,
            PrimaryActorId: string.Empty,
            DeploymentStatus: string.Empty,
            Endpoints: [],
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"));
}
