using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
            "Workflow capabilities are startup artifacts derived from module and connector capability sources outside projection stores.");
        typeof(IProjectionReadModel).IsAssignableFrom(typeof(WorkflowCapabilitiesStartupArtifact))
            .Should()
            .BeFalse("startup capabilities are not actor-scoped current-state readmodels");
        typeof(WorkflowCapabilitiesStartupArtifact).GetInterfaces()
            .Should()
            .NotContain(type =>
                type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IProjectionReadModel<>));
        typeof(IProjectionReadModel).IsAssignableFrom(typeof(WorkflowCapabilitiesStartupArtifact))
            .Should()
            .BeFalse("startup capabilities must not re-enter projection store version/conflict semantics");
    }
}
