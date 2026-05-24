using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionExemptionTests
{
    [Fact]
    public void WorkflowCapabilitiesCurrentStateDocument_ShouldDeclareStartupBootstrapExemption()
    {
        var exemption = typeof(WorkflowCapabilitiesCurrentStateDocument)
            .GetCustomAttribute<ProjectionExemptAttribute>(inherit: false);

        exemption.Should().NotBeNull();
        exemption!.Category.Should().Be(ProjectionExemptionCategory.StartupBootstrap);
        exemption.Reason.Should().Be(
            "Workflow capabilities are materialized by WorkflowCapabilitiesStartupMaterializer from startup module and connector capability sources.");
    }
}
