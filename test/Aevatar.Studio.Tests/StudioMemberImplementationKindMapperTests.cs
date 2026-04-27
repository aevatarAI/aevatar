using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberImplementationKindMapperTests
{
    [Theory]
    [InlineData("workflow", StudioMemberImplementationKind.Workflow)]
    [InlineData("WORKFLOW", StudioMemberImplementationKind.Workflow)]
    [InlineData("script", StudioMemberImplementationKind.Script)]
    [InlineData("gagent", StudioMemberImplementationKind.Gagent)]
    public void Parse_ShouldMapKnownKindsCaseInsensitive(string wire, StudioMemberImplementationKind expected)
    {
        MemberImplementationKindMapper.Parse(wire).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ShouldRejectMissingValue(string? wire)
    {
        var act = () => MemberImplementationKindMapper.Parse(wire);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*implementationKind is required*");
    }

    [Fact]
    public void Parse_ShouldRejectUnknownValue()
    {
        var act = () => MemberImplementationKindMapper.Parse("worker");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown implementationKind*");
    }

    [Theory]
    [InlineData(StudioMemberImplementationKind.Workflow, "workflow")]
    [InlineData(StudioMemberImplementationKind.Script, "script")]
    [InlineData(StudioMemberImplementationKind.Gagent, "gagent")]
    public void ToWireName_ImplementationKind_ShouldMapBack(
        StudioMemberImplementationKind kind, string expected)
    {
        MemberImplementationKindMapper.ToWireName(kind).Should().Be(expected);
    }

    [Theory]
    [InlineData(StudioMemberLifecycleStage.Created, "created")]
    [InlineData(StudioMemberLifecycleStage.BuildReady, "build_ready")]
    [InlineData(StudioMemberLifecycleStage.BindReady, "bind_ready")]
    public void ToWireName_LifecycleStage_ShouldMapToWireName(
        StudioMemberLifecycleStage stage, string expected)
    {
        MemberImplementationKindMapper.ToWireName(stage).Should().Be(expected);
    }

    [Fact]
    public void ToWireName_LifecycleStage_ShouldReturnEmpty_ForUnspecified()
    {
        // An Unspecified value indicates a malformed projection; previously
        // this silently mapped to "created", which lied to callers.
        MemberImplementationKindMapper
            .ToWireName(StudioMemberLifecycleStage.Unspecified)
            .Should().BeEmpty();
    }
}
