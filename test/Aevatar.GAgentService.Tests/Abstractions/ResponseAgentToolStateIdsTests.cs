using Aevatar.GAgentService.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class ResponseAgentToolStateIdsTests
{
    [Theory]
    [InlineData("scope/with/slashes", "owner:with:colons")]
    [InlineData(" scope with whitespace ", "owner\twith\ncontrol\rchars")]
    [InlineData("scope-_.AZaz09", "owner.09_AZ-az")]
    [InlineData("scope/emoji/😀", "owner:emoji:🚀")]
    [InlineData("scope\u0001control", "owner\u001Fcontrol")]
    public void BuildReadableActorId_ShouldRoundTrip_WhenIdFitsLengthCap(string scopeId, string ownerSubject)
    {
        var actorId = ResponseAgentToolStateIds.BuildActorId(scopeId, ownerSubject, readableIdsEnabled: true);

        actorId.Should().StartWith("responses-agent-tools/scope:");
        actorId.Length.Should().BeLessThanOrEqualTo(ResponseAgentToolStateIds.MaxActorIdLength);
        ResponseAgentToolStateIds.TryDecodeReadableActorId(actorId, out var decodedScope, out var decodedOwner)
            .Should().BeTrue();
        decodedScope.Should().Be(scopeId);
        decodedOwner.Should().Be(ownerSubject);
    }

    [Fact]
    public void BuildReadableActorId_ShouldPercentEncodePerRfc3986Subset()
    {
        var actorId = ResponseAgentToolStateIds.BuildReadableActorId("a/b c:😀", "owner/value");

        actorId.Should().Be("responses-agent-tools/scope:a%2Fb%20c%3A%F0%9F%98%80/owner:owner%2Fvalue");
    }

    [Fact]
    public void BuildReadableActorId_ShouldCapLongIds_WithDeterministicHashTail()
    {
        var scopeId = new string('s', 700);
        var ownerSubject = new string('o', 900);

        var first = ResponseAgentToolStateIds.BuildReadableActorId(scopeId, ownerSubject);
        var second = ResponseAgentToolStateIds.BuildReadableActorId(scopeId, ownerSubject);

        first.Length.Should().Be(ResponseAgentToolStateIds.MaxActorIdLength);
        second.Should().Be(first);
        first.Should().MatchRegex("~[0-9a-f]{16}(/owner:|$)");
    }

    [Fact]
    public void BuildActorId_WithFlagOff_ShouldProduceLegacyHashByteForByte()
    {
        var actorId = ResponseAgentToolStateIds.BuildActorId("scope-1", "owner-1", readableIdsEnabled: false);

        actorId.Should().Be("responses-agent-tools-1662ddfc14b325e0f1599f72839ada33");
        actorId.Should().Be(ResponseAgentToolStateIds.BuildLegacyActorId("scope-1", "owner-1"));
    }

    [Fact]
    public void BuildActorId_WithFlagOn_ShouldProduceReadableId()
    {
        var actorId = ResponseAgentToolStateIds.BuildActorId("scope/1", "owner:1", readableIdsEnabled: true);

        actorId.Should().Be("responses-agent-tools/scope:scope%2F1/owner:owner%3A1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("responses-agent-tools/scope:abc")]
    [InlineData("responses-agent-tools/scope:abc/owner:%ZZ")]
    [InlineData("responses-agent-tools/scope:abc/owner:😀")]
    public void TryDecodeReadableActorId_ShouldRejectInvalidIds(string actorId)
    {
        ResponseAgentToolStateIds.TryDecodeReadableActorId(actorId, out var scopeId, out var ownerSubject)
            .Should().BeFalse();
        scopeId.Should().BeEmpty();
        ownerSubject.Should().BeEmpty();
    }
}
