using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceReadModelCloneTests
{
    [Fact]
    public void ServiceCatalogReadModel_Clone_ShouldCopyNestedEndpoints()
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

        var clone = source.Clone();
        clone.Endpoints[0].DisplayName = "changed";

        source.Endpoints[0].DisplayName.Should().Be("run");
    }

    [Fact]
    public void ServiceRevisionCatalogReadModel_Clone_ShouldCopyNestedRevisionEntries()
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

        var clone = source.Clone();
        clone.Revisions[0].Endpoints[0].DisplayName = "changed";

        source.Revisions[0].Endpoints[0].DisplayName.Should().Be("run");
    }

    [Fact]
    public void ServiceDeploymentCatalogReadModel_Clone_ShouldCopyNestedDeployments()
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

        var clone = source.Clone();
        clone.Deployments[0].RevisionId = "changed";

        source.Deployments[0].RevisionId.Should().Be("r1");
    }

    [Fact]
    public void ServiceServingSetReadModel_Clone_ShouldCopyNestedTargets()
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

        var clone = source.Clone();
        clone.Targets[0].EnabledEndpointIds[0] = "changed";

        source.Targets[0].EnabledEndpointIds.Should().Equal("run");
    }

    [Fact]
    public void ServiceRolloutReadModel_Clone_ShouldCopyStagesAndBaselineTargets()
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

        var clone = source.Clone();
        clone.Stages[0].Targets[0].DeploymentId = "changed-stage";
        clone.BaselineTargets[0].DeploymentId = "changed-baseline";

        source.Stages[0].Targets[0].DeploymentId.Should().Be("dep-1");
        source.BaselineTargets[0].DeploymentId.Should().Be("dep-base");
    }

    [Fact]
    public void ServiceTrafficViewReadModel_Clone_ShouldCopyEndpointsAndTargets()
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

        var clone = source.Clone();
        clone.Endpoints[0].Targets[0].DeploymentId = "changed";

        source.Endpoints[0].Targets[0].DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public void ServiceConfigurationReadModel_Clone_ShouldCopyBindingsPoliciesAndEndpoints()
    {
        var source = new ServiceConfigurationReadModel
        {
            Id = "svc",
            Bindings =
            {
                new ServiceBindingReadModel
                {
                    BindingId = "binding-a",
                    PolicyIds = { "policy-a" },
                    ConnectorRef = new BoundConnectorReferenceReadModel
                    {
                        ConnectorType = "mcp",
                        ConnectorId = "connector-a",
                    },
                },
            },
            Endpoints =
            {
                new ServiceEndpointExposureReadModel
                {
                    EndpointId = "invoke",
                    PolicyIds = { "policy-a" },
                },
            },
            Policies =
            {
                new ServicePolicyReadModel
                {
                    PolicyId = "policy-a",
                    ActivationRequiredBindingIds = { "binding-a" },
                },
            },
        };

        var clone = source.Clone();
        clone.Bindings[0].PolicyIds[0] = "changed-binding-policy";
        clone.Bindings[0].ConnectorRef!.ConnectorId = "changed-connector";
        clone.Endpoints[0].PolicyIds[0] = "changed-endpoint-policy";
        clone.Policies[0].ActivationRequiredBindingIds[0] = "changed-binding";

        source.Bindings[0].PolicyIds.Should().Equal("policy-a");
        source.Bindings[0].ConnectorRef!.ConnectorId.Should().Be("connector-a");
        source.Endpoints[0].PolicyIds.Should().Equal("policy-a");
        source.Policies[0].ActivationRequiredBindingIds.Should().Equal("binding-a");
    }
}
