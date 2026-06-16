using Aevatar.AI.Abstractions.SkillInvocations;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentSkillRecoveryContextBuilderTests
{
    [Fact]
    public void FromTrigger_WhenNamedTrigger_ShouldBuildSkillRecoveryContext()
    {
        SkillInvocationTriggerParser.TryParse("::Goal ship today", "cli", out var trigger).Should().BeTrue();

        var context = AgentSkillRecoveryContextBuilder.FromTrigger(trigger);

        context.RequireInitialOrnnSearch.Should().BeTrue();
        context.RequireOrnnSearchOnBlocker.Should().BeTrue();
        context.CommandName.Should().Be("goal");
        context.OriginalCommand.Should().Be("::Goal ship today");
        context.PrimarySkillName.Should().Be("goal");
        context.MaxOrnnSearchAttempts.Should().Be(2);
        context.CommandArguments.Should().Be("ship today");
        context.DiscoveryRequested.Should().BeFalse();
    }

    [Fact]
    public void FromTrigger_WhenDiscoveryTrigger_ShouldBuildDiscoveryRecoveryContext()
    {
        SkillInvocationTriggerParser.TryParse("::", "cli", out var trigger).Should().BeTrue();

        var context = AgentSkillRecoveryContextBuilder.FromTrigger(trigger);

        context.RequireInitialOrnnSearch.Should().BeTrue();
        context.RequireOrnnSearchOnBlocker.Should().BeFalse();
        context.CommandName.Should().BeNull();
        context.OriginalCommand.Should().Be("::");
        context.PrimarySkillName.Should().BeNull();
        context.MaxOrnnSearchAttempts.Should().Be(1);
        context.CommandArguments.Should().BeNull();
        context.DiscoveryRequested.Should().BeTrue();
    }
}
