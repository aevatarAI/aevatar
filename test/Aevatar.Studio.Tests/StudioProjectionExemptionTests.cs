using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Studio.Projection.Projectors;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioProjectionExemptionTests
{
    private const string StartupBootstrapReason =
        "Studio actor-backed store current-state readmodels are activated by StudioCurrentStateProjectionPort/StudioActorBootstrap; provider migration is tracked separately from issue #895.";

    [Theory]
    [MemberData(nameof(StartupBootstrapProjectors))]
    public void StartupBootstrapProjectors_ShouldDeclareProjectionExemption(Type projectorType)
    {
        var exemption = projectorType
            .GetCustomAttribute<ProjectionExemptAttribute>(inherit: false);

        exemption.Should().NotBeNull();
        exemption!.Category.Should().Be(ProjectionExemptionCategory.StartupBootstrap);
        exemption.Reason.Should().Be(StartupBootstrapReason);
    }

    public static TheoryData<Type> StartupBootstrapProjectors() =>
        new()
        {
            typeof(ChatConversationCurrentStateProjector),
            typeof(ChatHistoryIndexCurrentStateProjector),
            typeof(ConnectorCatalogCurrentStateProjector),
            typeof(GAgentRegistryCurrentStateProjector),
            typeof(RoleCatalogCurrentStateProjector),
            typeof(StudioMemberBindingRunCurrentStateProjector),
            typeof(StudioMemberCurrentStateProjector),
            typeof(StudioTeamCurrentStateProjector),
            typeof(StudioWorkspaceCurrentStateProjector),
            typeof(UserConfigCurrentStateProjector),
            typeof(UserMemoryCurrentStateProjector),
        };
}
