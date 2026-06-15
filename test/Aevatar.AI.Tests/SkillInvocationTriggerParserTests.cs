using Aevatar.AI.Abstractions.SkillInvocations;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class SkillInvocationTriggerParserTests
{
    [Fact]
    public void TryParse_WhenCanonicalNameAtStart_ShouldReturnNameAndArguments()
    {
        SkillInvocationTriggerParser.TryParse("::Goal ship today", "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("goal");
        trigger.Arguments.Should().Be("ship today");
        trigger.IsDiscovery.Should().BeFalse();
        trigger.OriginalText.Should().Be("::Goal ship today");
        trigger.TriggerToken.Should().Be("::");
        trigger.Platform.Should().Be("cli");
    }

    [Fact]
    public void TryParse_WhenTriggerStartsLaterLine_ShouldReturnFirstLegalTrigger()
    {
        var text = "please inspect this\n::alpha first\n::beta second";

        SkillInvocationTriggerParser.TryParse(text, "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("alpha");
        trigger.Arguments.Should().Be("first");
    }

    [Fact]
    public void TryParse_WhenFirstLineHasIllegalTrigger_ShouldReturnFirstLegalLaterTrigger()
    {
        var text = "please run ::goal today\n::alpha first";

        SkillInvocationTriggerParser.TryParse(text, "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("alpha");
        trigger.Arguments.Should().Be("first");
    }

    [Fact]
    public void TryParse_WhenTriggerAppearsInsideSentence_ShouldIgnoreIt()
    {
        SkillInvocationTriggerParser.TryParse("please run ::goal today", "cli", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenCanonicalBareToken_ShouldRequestDiscovery()
    {
        SkillInvocationTriggerParser.TryParse("::", "cli", out var trigger).Should().BeTrue();

        trigger.IsDiscovery.Should().BeTrue();
        trigger.Name.Should().BeNull();
        trigger.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenCanonicalTokenHasTrailingWhitespace_ShouldIgnoreIt()
    {
        SkillInvocationTriggerParser.TryParse(":: ", "cli", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("web")]
    public void TryParse_WhenSlashAliasAllowedForPlatform_ShouldReturnName(string platform)
    {
        SkillInvocationTriggerParser.TryParse("/Goal args", platform, out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("goal");
        trigger.Arguments.Should().Be("args");
        trigger.TriggerToken.Should().Be("/");
    }

    [Fact]
    public void TryParse_WhenSlashAliasOnCli_ShouldReject()
    {
        SkillInvocationTriggerParser.TryParse("/goal args", "cli", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenSlashAliasOnDefaultPlatform_ShouldReject()
    {
        SkillInvocationTriggerParser.TryParse("/goal args", null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenOptionsCustomizePlatformAliases_ShouldUseConfiguredTokens()
    {
        var options = new SkillInvocationTriggerOptions();
        options.SetPlatformTokens("custom", ["!!"]);

        SkillInvocationTriggerParser.TryParse("!!Goal args", "custom", out var trigger, options).Should().BeTrue();

        trigger.Name.Should().Be("goal");
        trigger.Arguments.Should().Be("args");
        trigger.TriggerToken.Should().Be("!!");
    }

    [Fact]
    public void TryParse_WhenNameLooksLikeConcreteFixture_ShouldStillUseGenericNameParsing()
    {
        SkillInvocationTriggerParser.TryParse("::chrono-ai-daily alice", "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("chrono-ai-daily");
        trigger.Arguments.Should().Be("alice");
    }
}
