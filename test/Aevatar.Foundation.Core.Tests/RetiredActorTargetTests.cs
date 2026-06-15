using Aevatar.Foundation.Abstractions.Maintenance;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RetiredActorTargetTests
{
    [Fact]
    public void Ctor_ShouldAcceptFullyQualifiedTokens()
    {
        var act = () => new RetiredActorTarget(
            "agent-id",
            ["Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_ShouldRejectBareTokenWithoutNamespace()
    {
        // A bare token would match any TypeName ending with "GAgent" because the
        // boundary set intentionally excludes '.'. Reject so specs can't ship a
        // foot-gun that looks like it works on the well-known IDs.
        var act = () => new RetiredActorTarget("agent-id", ["GAgent"]);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*fully-qualified*");
    }

    [Fact]
    public void Ctor_ShouldRejectEmptyTokenList()
    {
        var act = () => new RetiredActorTarget("agent-id", Array.Empty<string>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_ShouldRejectWhitespaceToken()
    {
        var act = () => new RetiredActorTarget("agent-id", new[] { "Aevatar.Real.Type", "  " });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MatchesRuntimeType_ShouldMatchFullyQualifiedToken()
    {
        var target = new RetiredActorTarget(
            "agent-id",
            ["Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent"]);

        target
            .MatchesRuntimeType("Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent, Aevatar.GAgents.ChannelRuntime")
            .Should()
            .BeTrue();
    }

    [Fact]
    public void MatchesRuntimeType_ShouldRejectProxyOrSubstringTypeNames()
    {
        var target = new RetiredActorTarget(
            "agent-id",
            ["Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent"]);

        target
            .MatchesRuntimeType("Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgentProxy, Aevatar")
            .Should()
            .BeFalse();
    }
}
