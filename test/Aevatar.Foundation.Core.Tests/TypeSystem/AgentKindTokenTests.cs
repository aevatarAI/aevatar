using Aevatar.Foundation.Abstractions.TypeSystem;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.TypeSystem;

public class AgentKindTokenTests
{
    [Theory]
    [InlineData("scheduled.skill-runner")]
    [InlineData("scheduled.skill-definition")]
    [InlineData("channels.bot-registration")]
    [InlineData("users.agent-catalog")]
    [InlineData("module.entity-name")]
    [InlineData("a.b")]
    [InlineData("a1.b2-c3")]
    [InlineData("foo.bar.baz")]
    public void IsValid_AcceptsConventionalKinds(string kind)
    {
        AgentKindToken.IsValid(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("noModule")]
    [InlineData("Module.entity")] // capital
    [InlineData("module.Entity")]
    [InlineData("module..double-dot")]
    [InlineData(".module.entity")]
    [InlineData("module.entity.")]
    [InlineData("module.entity-")]
    [InlineData("module.-entity")]
    [InlineData("scheduled.skill-runner-v2")]   // versioned tail forbidden
    [InlineData("scheduled.skill-runner-v10")]
    public void IsValid_RejectsMalformedKinds(string kind)
    {
        AgentKindToken.IsValid(kind).Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsVersionedTailWithSpecificError()
    {
        var act = () => AgentKindToken.Validate("scheduled.skill-runner-v2");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*never versioned*");
    }

    [Fact]
    public void Validate_RejectsFormatMismatchWithSpecificError()
    {
        var act = () => AgentKindToken.Validate("Bad.Format");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*scheduled.skill-runner*");
    }
}
