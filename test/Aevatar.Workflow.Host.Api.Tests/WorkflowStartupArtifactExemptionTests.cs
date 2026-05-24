using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowStartupArtifactExemptionTests
{
    [Fact]
    public void WorkflowCapabilitiesStartupArtifact_ShouldDeclareStartupBootstrapExemption()
    {
        var exemption = typeof(WorkflowCapabilitiesStartupArtifact)
            .GetCustomAttribute<ProjectionExemptAttribute>(inherit: false);

        exemption.Should().NotBeNull();
        exemption!.Category.Should().Be(ProjectionExemptionCategory.StartupBootstrap);
        exemption.Reason.Should().Be(
            "Workflow capabilities are startup artifacts materialized by WorkflowCapabilitiesStartupMaterializer from module and connector capability sources.");
    }
}
