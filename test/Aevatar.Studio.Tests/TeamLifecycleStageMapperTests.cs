using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Mapping;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class TeamLifecycleStageMapperTests
{
    [Fact]
    public void ToWireName_Active_ShouldReturnActiveString()
    {
        TeamLifecycleStageMapper.ToWireName(StudioTeamLifecycleStage.Active)
            .Should().Be(TeamLifecycleStageNames.Active);
    }

    [Fact]
    public void ToWireName_Archived_ShouldReturnArchivedString()
    {
        TeamLifecycleStageMapper.ToWireName(StudioTeamLifecycleStage.Archived)
            .Should().Be(TeamLifecycleStageNames.Archived);
    }

    [Fact]
    public void ToWireName_Unspecified_ShouldReturnEmptyString()
    {
        TeamLifecycleStageMapper.ToWireName(StudioTeamLifecycleStage.Unspecified)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData((StudioTeamLifecycleStage)99)]
    [InlineData((StudioTeamLifecycleStage)(-1))]
    public void ToWireName_UnknownValue_ShouldReturnEmptyString(StudioTeamLifecycleStage stage)
    {
        TeamLifecycleStageMapper.ToWireName(stage)
            .Should().BeEmpty();
    }
}
