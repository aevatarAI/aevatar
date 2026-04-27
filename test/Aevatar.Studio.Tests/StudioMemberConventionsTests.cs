using Aevatar.GAgents.StudioMember;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberConventionsTests
{
    [Fact]
    public void BuildActorId_ShouldFollowCanonicalLayout()
    {
        var actorId = StudioMemberConventions.BuildActorId("scope-1", "m-abc");
        actorId.Should().Be("studio-member:scope-1:m-abc");
    }

    [Fact]
    public void BuildActorId_ShouldRejectMissingScope()
    {
        var act = () => StudioMemberConventions.BuildActorId("   ", "m-abc");
        act.Should().Throw<ArgumentException>().WithParameterName("scopeId");
    }

    [Fact]
    public void BuildActorId_ShouldRejectMissingMember()
    {
        var act = () => StudioMemberConventions.BuildActorId("scope-1", null!);
        act.Should().Throw<ArgumentException>().WithParameterName("memberId");
    }

    [Fact]
    public void BuildPublishedServiceId_ShouldOnlyDependOnMemberId()
    {
        // Renaming display name later must not affect publishedServiceId,
        // which is enforced by deriving it solely from the immutable id.
        var first = StudioMemberConventions.BuildPublishedServiceId("m-abc");
        var second = StudioMemberConventions.BuildPublishedServiceId("m-abc");
        first.Should().Be(second);
        first.Should().Be("member-m-abc");
    }

    [Fact]
    public void BuildPublishedServiceId_ShouldDifferAcrossMembers()
    {
        var a = StudioMemberConventions.BuildPublishedServiceId("m-aaa");
        var b = StudioMemberConventions.BuildPublishedServiceId("m-bbb");
        a.Should().NotBe(b);
    }

    [Fact]
    public void NormalizeScopeId_ShouldRejectActorIdSeparator()
    {
        // Allowing ':' would let a caller forge actor IDs by collision —
        // e.g. "scope-1:m-evil" would round-trip to the actor id of a real
        // member in scope-1. Reject at the boundary.
        var act = () => StudioMemberConventions.NormalizeScopeId("scope:1");
        act.Should().Throw<ArgumentException>().WithParameterName("scopeId");
    }

    [Fact]
    public void NormalizeMemberId_ShouldRejectActorIdSeparator()
    {
        var act = () => StudioMemberConventions.NormalizeMemberId("m:nested");
        act.Should().Throw<ArgumentException>().WithParameterName("memberId");
    }

    [Fact]
    public void BuildActorId_ShouldRejectActorIdSeparatorInMemberId()
    {
        var act = () => StudioMemberConventions.BuildActorId("scope-1", "m:nested");
        act.Should().Throw<ArgumentException>().WithParameterName("memberId");
    }
}
