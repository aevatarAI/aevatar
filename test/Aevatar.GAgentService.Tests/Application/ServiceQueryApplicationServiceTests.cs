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
        var revisionReader = new RecordingRevisionReader();
        var service = new ServiceQueryApplicationService(catalogReader, revisionReader);

        _ = await service.GetServiceAsync(identity);

        catalogReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task GetServiceRevisionsAsync_ShouldDelegateToRevisionReader()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var catalogReader = new RecordingCatalogReader();
        var revisionReader = new RecordingRevisionReader();
        var service = new ServiceQueryApplicationService(catalogReader, revisionReader);

        _ = await service.GetServiceRevisionsAsync(identity);

        revisionReader.Identities.Should().ContainSingle(x => x.ServiceId == "svc");
    }

    [Fact]
    public async Task ListServicesAsync_ShouldDelegateToCatalogReader()
    {
        var catalogReader = new RecordingCatalogReader();
        var revisionReader = new RecordingRevisionReader();
        var service = new ServiceQueryApplicationService(catalogReader, revisionReader);

        _ = await service.ListServicesAsync("tenant", "app", "ns", take: 42);

        catalogReader.ListCalls.Should().ContainSingle();
        catalogReader.ListCalls[0].Should().Be(("tenant", "app", "ns", 42));
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
}
