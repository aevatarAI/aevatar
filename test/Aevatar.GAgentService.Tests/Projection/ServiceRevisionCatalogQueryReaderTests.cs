using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceRevisionCatalogQueryReaderTests
{
    [Fact]
    public async Task GetAsync_ShouldSortRevisionsByCreatedAtDescending()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r1",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r2",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel
                        {
                            EndpointId = "run",
                            DisplayName = "run",
                            Kind = "Command",
                        },
                    },
                },
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.Revisions.Select(x => x.RevisionId).Should().Equal("r2", "r1");
        snapshot.Revisions[0].Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenCatalogDoesNotExist()
    {
        var reader = new ServiceRevisionCatalogQueryReader(new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id));

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldValidateDocumentStore()
    {
        Action act = () => new ServiceRevisionCatalogQueryReader(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeEmptyEndpointFields()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r-empty",
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel(),
                    },
                },
            },
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.Revisions.Should().ContainSingle();
        snapshot.Revisions[0].Endpoints.Should().ContainSingle();
        snapshot.Revisions[0].Endpoints[0].EndpointId.Should().BeEmpty();
    }
}
