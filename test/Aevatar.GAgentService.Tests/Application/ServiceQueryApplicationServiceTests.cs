using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceQueryApplicationServiceTests
{
    [Fact]
    public async Task GetServiceAsync_ShouldDelegateToCatalogReader()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalogReader = new RecordingCatalogReader();
        var service = CreateService(catalogReader);

        _ = await service.GetServiceAsync(identity);

        catalogReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task GetServiceRevisionsAsync_ShouldDelegateToRevisionReader()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionReader = new RecordingRevisionReader();
        var service = CreateService(new RecordingCatalogReader(), revisionReader: revisionReader);

        _ = await service.GetServiceRevisionsAsync(identity);

        revisionReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task GetPhase3Queries_ShouldDelegateToDedicatedReaders()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var deploymentReader = new RecordingDeploymentReader();
        var servingReader = new RecordingServingReader();
        var rolloutReader = new RecordingRolloutReader();
        var trafficReader = new RecordingTrafficReader();
        var service = CreateService(
            new RecordingCatalogReader(),
            new RecordingRevisionReader(),
            deploymentReader,
            servingReader,
            rolloutReader,
            trafficReader);

        _ = await service.GetServiceDeploymentsAsync(identity);
        _ = await service.GetServiceServingSetAsync(identity);
        _ = await service.GetServiceRolloutAsync(identity);
        _ = await service.GetServiceTrafficViewAsync(identity);

        deploymentReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        servingReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        rolloutReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
        trafficReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task ListServicesAsync_ShouldDelegateToCatalogReader()
    {
        var catalogReader = new RecordingCatalogReader();
        var service = CreateService(catalogReader);

        _ = await service.ListServicesAsync("tenant", "app", "ns", take: 42);

        catalogReader.ListCalls.Should().ContainSingle();
        catalogReader.ListCalls[0].Should().Be(("tenant", "app", "ns", 42));
    }

    private static ServiceQueryApplicationService CreateService(
        RecordingCatalogReader catalogReader,
        RecordingRevisionReader? revisionReader = null,
        RecordingDeploymentReader? deploymentReader = null,
        RecordingServingReader? servingReader = null,
        RecordingRolloutReader? rolloutReader = null,
        RecordingTrafficReader? trafficReader = null) =>
        new(
            catalogReader,
            revisionReader ?? new RecordingRevisionReader(),
            deploymentReader ?? new RecordingDeploymentReader(),
            servingReader ?? new RecordingServingReader(),
            rolloutReader ?? new RecordingRolloutReader(),
            trafficReader ?? new RecordingTrafficReader());

    private sealed class RecordingCatalogReader : IServiceCatalogQueryReader
    {
        public List<ServiceIdentity> Identities { get; } = [];
        public List<(string tenantId, string appId, string @namespace, int take)> ListCalls { get; } = [];

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult<ServiceCatalogSnapshot?>(null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
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
}
