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

    [Fact]
    public void ServiceDeploymentCatalogReadModel_DeepClone_ShouldCopyNestedDeployments()
    {
        var source = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc",
            Deployments =
            {
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-1",
                    RevisionId = "r1",
                    PrimaryActorId = "actor-1",
                },
            },
        };

        var clone = source.DeepClone();
        clone.Deployments[0].RevisionId = "changed";

        source.Deployments[0].RevisionId.Should().Be("r1");
    }

    [Fact]
    public void ServiceServingSetReadModel_DeepClone_ShouldCopyNestedTargets()
    {
        var source = new ServiceServingSetReadModel
        {
            Id = "svc",
            Targets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-1",
                    EnabledEndpointIds = { "run" },
                },
            },
        };

        var clone = source.DeepClone();
        clone.Targets[0].EnabledEndpointIds[0] = "changed";

        source.Targets[0].EnabledEndpointIds.Should().Equal("run");
    }

    [Fact]
    public void ServiceRolloutReadModel_DeepClone_ShouldCopyStagesAndBaselineTargets()
    {
        var source = new ServiceRolloutReadModel
        {
            Id = "svc",
            Stages =
            {
                new ServiceRolloutStageReadModel
                {
                    StageId = "stage-a",
                    Targets =
                    {
                        new ServiceServingTargetReadModel
                        {
                            DeploymentId = "dep-1",
                        },
                    },
                },
            },
            BaselineTargets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-base",
                },
            },
        };

        var clone = source.DeepClone();
        clone.Stages[0].Targets[0].DeploymentId = "changed-stage";
        clone.BaselineTargets[0].DeploymentId = "changed-baseline";

        source.Stages[0].Targets[0].DeploymentId.Should().Be("dep-1");
        source.BaselineTargets[0].DeploymentId.Should().Be("dep-base");
    }

    [Fact]
    public void ServiceTrafficViewReadModel_DeepClone_ShouldCopyEndpointsAndTargets()
    {
        var source = new ServiceTrafficViewReadModel
        {
            Id = "svc",
            Endpoints =
            {
                new ServiceTrafficEndpointReadModel
                {
                    EndpointId = "run",
                    Targets =
                    {
                        new ServiceTrafficTargetReadModel
                        {
                            DeploymentId = "dep-1",
                        },
                    },
                },
            },
        };

        var clone = source.DeepClone();
        clone.Endpoints[0].Targets[0].DeploymentId = "changed";

        source.Endpoints[0].Targets[0].DeploymentId.Should().Be("dep-1");
    }
}
