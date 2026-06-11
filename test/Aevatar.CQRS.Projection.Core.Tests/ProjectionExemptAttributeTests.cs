using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionExemptAttributeTests
{
    [Fact]
    public void ProjectionExemptAttribute_ShouldDeclareClassOnlyNonInheritedContract()
    {
        var usage = typeof(ProjectionExemptAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .Should().ContainSingle().Subject.As<AttributeUsageAttribute>();

        usage.ValidOn.Should().Be(AttributeTargets.Class);
        usage.AllowMultiple.Should().BeFalse();
        usage.Inherited.Should().BeFalse();
    }

    [Fact]
    public void ProjectionScopeStatusProjector_ShouldDeclareProjectionCoreStatusExemption()
    {
        var exemption = typeof(ProjectionScopeStatusProjector)
            .GetCustomAttributes(typeof(ProjectionExemptAttribute), inherit: false)
            .Should().ContainSingle().Subject.As<ProjectionExemptAttribute>();

        exemption.Category.Should().Be(ProjectionExemptionCategory.ProjectionCoreStatus);
        exemption.Reason.Should().Be(
            "Projection runtime status is activated internally when projection scopes start; it is not a feature readmodel with a committed-state plan provider.");
    }

    [Fact]
    public void ProjectionExemptAttribute_ShouldExposeMutableCategoryAndDefaultReason()
    {
        var exemption = new ProjectionExemptAttribute
        {
            Category = ProjectionExemptionCategory.TestOnly,
        };

        exemption.Category.Should().Be(ProjectionExemptionCategory.TestOnly);
        exemption.Reason.Should().BeEmpty();

        exemption.Reason = "test-only exemption";

        exemption.Reason.Should().Be("test-only exemption");
    }

    [Fact]
    public void ProjectionExemptionCategory_ShouldKeepStableGuardCategories()
    {
        ((int)ProjectionExemptionCategory.StartupBootstrap).Should().Be(1);
        ((int)ProjectionExemptionCategory.SessionObservation).Should().Be(2);
        ((int)ProjectionExemptionCategory.ArtifactNotCurrentState).Should().Be(3);
        ((int)ProjectionExemptionCategory.ProjectionCoreStatus).Should().Be(4);
        ((int)ProjectionExemptionCategory.TestOnly).Should().Be(5);
        ((int)ProjectionExemptionCategory.LegacyToDelete).Should().Be(6);
    }
}
