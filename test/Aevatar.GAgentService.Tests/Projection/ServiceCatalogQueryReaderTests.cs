using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Configuration;
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
            StateVersion = 42,
            LastEventId = "event-42",
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
            ExternalExposure = new ServiceCatalogExternalExposureReadModel
            {
                NyxidSlug = "aevatar-orders",
                RegisteredAt = DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"),
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var reader = new ServiceCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.ServiceKey.Should().Be("tenant:app:default:svc");
        snapshot.StateVersion.Should().Be(42);
        snapshot.LastEventId.Should().Be("event-42");
        snapshot.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        snapshot.ExternalExposure.Should().NotBeNull();
        snapshot.ExternalExposure.NyxidSlug.Should().Be("aevatar-orders");
        snapshot.ExternalExposure.RegisteredAt.Should().Be(DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"));
        snapshot.ExternalExposure.SourceStateVersion.Should().Be(42);
    }

    [Fact]
    public async Task GetAsync_ShouldMapFailedExposureWithoutSlugOrRegisteredAt()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
            StateVersion = 7,
            ExternalExposure = new ServiceCatalogExternalExposureReadModel
            {
                Status = ServiceRegistrationStatus.Failed,
                DesiredSpecHash = "hash-1",
                LastError = "retry_exhausted:MissingToken:missing_registration_token",
                Attempt = 3,
                ExposureDesired = true,
            },
        });
        var reader = new ServiceCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.ExternalExposure.Should().NotBeNull();
        snapshot.ExternalExposure!.Status.Should().Be(ServiceRegistrationStatus.Failed);
        snapshot.ExternalExposure.LastError.Should().Be("retry_exhausted:MissingToken:missing_registration_token");
        snapshot.ExternalExposure.SourceStateVersion.Should().Be(7);
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

    [Fact]
    public async Task QueryAllAsync_ShouldClampTakeBounds_AndMapSnapshots()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc-a",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc-a",
            DisplayName = "Service A",
        });
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc-b",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc-b",
            DisplayName = "Service B",
        });
        var reader = new ServiceCatalogQueryReader(
            store,
            new ServiceProjectionOptions
            {
                Enabled = true,
            });

        var boundedToMinimum = await reader.QueryAllAsync(take: 0);
        store.LastQueryTake.Should().Be(1);
        boundedToMinimum.Should().ContainSingle();
        boundedToMinimum[0].ServiceKey.Should().Be("tenant:app:default:svc-a");

        var boundedToMaximum = await reader.QueryAllAsync(take: 20_000);
        store.LastQueryTake.Should().Be(10_000);
        boundedToMaximum.Select(x => x.ServiceKey).Should().Equal("tenant:app:default:svc-a", "tenant:app:default:svc-b");
    }

    [Fact]
    public async Task QueryReader_ShouldReturnNullOrEmpty_WhenProjectionDisabled()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        });
        var reader = new ServiceCatalogQueryReader(
            store,
            new ServiceProjectionOptions
            {
                Enabled = false,
            });

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());
        var all = await reader.QueryAllAsync();
        var scoped = await reader.QueryByScopeAsync("tenant", "app", "default");

        snapshot.Should().BeNull();
        all.Should().BeEmpty();
        scoped.Should().BeEmpty();
    }
}
