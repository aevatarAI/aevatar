using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceReadModelCloneTests
{
    [Fact]
    public void ServiceCatalogReadModel_DeepClone_ShouldCopyNestedEndpoints()
    {
        var source = new ServiceCatalogReadModel
        {
            Id = "svc",
            Endpoints =
            {
                new ServiceCatalogEndpointReadModel
                {
                    EndpointId = "run",
                    DisplayName = "run",
                },
            },
        };

        var clone = source.DeepClone();
        clone.Endpoints[0].DisplayName = "changed";

        source.Endpoints[0].DisplayName.Should().Be("run");
    }

    [Fact]
    public void ServiceRevisionCatalogReadModel_DeepClone_ShouldCopyNestedRevisionEntries()
    {
        var source = new ServiceRevisionCatalogReadModel
        {
            Id = "svc",
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r1",
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel
                        {
                            EndpointId = "run",
                            DisplayName = "run",
                        },
                    },
                },
            },
        };

        var clone = source.DeepClone();
        clone.Revisions[0].Endpoints[0].DisplayName = "changed";

        source.Revisions[0].Endpoints[0].DisplayName.Should().Be("run");
    }
}
