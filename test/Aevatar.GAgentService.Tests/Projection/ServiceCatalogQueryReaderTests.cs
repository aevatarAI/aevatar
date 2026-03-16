using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCatalogQueryReaderTests
{
    [Fact]
    public async Task GetAsync_ShouldMapServiceSnapshot()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
            DisplayName = "Service",
            Endpoints =
            {
                new ServiceCatalogEndpointReadModel
                {
                    EndpointId = "run",
                    DisplayName = "run",
                    Kind = "Command",
                    RequestTypeUrl = "type.googleapis.com/test.command",
                },
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var reader = new ServiceCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.ServiceKey.Should().Be("tenant:app:default:svc");
        snapshot.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
    }

    [Fact]
    public async Task QueryByScopeAsync_ShouldFilterByIdentityFields_AndClampTake()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc-a",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc-a",
        });
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc-b",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc-b",
        });
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:other:default:svc-c",
            TenantId = "tenant",
            AppId = "other",
            Namespace = "default",
            ServiceId = "svc-c",
        });
        var reader = new ServiceCatalogQueryReader(store);

        var snapshots = await reader.QueryByScopeAsync("tenant", "app", "default", take: 0);

        store.LastQueryTake.Should().Be(1);
        snapshots.Should().HaveCount(1);
        snapshots[0].ServiceKey.Should().Be("tenant:app:default:svc-a");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenReadModelDoesNotExist()
    {
        var reader = new ServiceCatalogQueryReader(new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id));

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task QueryByScopeAsync_ShouldClampTakeToUpperBound()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel { Id = "tenant:app:default:svc-a" });
        var reader = new ServiceCatalogQueryReader(store);

        _ = await reader.QueryByScopeAsync("tenant", "app", "default", take: 5000);

        store.LastQueryTake.Should().Be(1000);
    }
}
