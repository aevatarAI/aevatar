using Aevatar.Foundation.Abstractions.Maintenance;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RetiredActorTargetTests
{
    [Fact]
    public void Ctor_ShouldAcceptModuleQualifiedKindTokens()
    {
        var act = () => new RetiredActorTarget(
            "agent-id",
            ["channel-runtime.skill-runner"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_ShouldRejectBareTokenWithoutNamespace()
    {
        var act = () => new RetiredActorTarget("agent-id", ["skill-runner"]);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*module-qualified*");
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
        var act = () => new RetiredActorTarget("agent-id", new[] { "channel-runtime.skill-runner", "  " });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MatchesRuntimeKind_ShouldMatchPrimaryKindToken()
    {
        var target = new RetiredActorTarget(
            "agent-id",
            ["channel-runtime.user-agent-catalog"]);

        target
            .MatchesRuntimeKind("channel-runtime.user-agent-catalog")
            .Should()
            .BeTrue();
    }

    [Fact]
    public void MatchesRuntimeKind_ShouldRejectNonExactKind()
    {
        var target = new RetiredActorTarget(
            "agent-id",
            ["channel-runtime.user-agent-catalog"]);

        target
            .MatchesRuntimeKind("channel-runtime.user-agent-catalog-proxy")
            .Should()
            .BeFalse();
    }
}
