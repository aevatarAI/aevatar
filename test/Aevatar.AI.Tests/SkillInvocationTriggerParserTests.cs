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

    [Fact]
    public void TryParse_WhenSlashCommandHasMultiLineBody_ShouldKeepEntireBodyAsArguments()
    {
        var text = "/whatsapp-reply-draft Hi Everyone!\n\nBelow is a quick recap.\nHave a great summer!";

        SkillInvocationTriggerParser.TryParse(text, "lark", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("whatsapp-reply-draft");
        trigger.Arguments.Should().Be("Hi Everyone!\n\nBelow is a quick recap.\nHave a great summer!");
        trigger.TriggerToken.Should().Be("/");
    }

    [Fact]
    public void TryParse_WhenCanonicalCommandHasMultiLineBody_ShouldKeepEntireBodyAsArguments()
    {
        var text = "::goal first line\nsecond line\nthird line";

        SkillInvocationTriggerParser.TryParse(text, "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("goal");
        trigger.Arguments.Should().Be("first line\nsecond line\nthird line");
    }

    [Fact]
    public void TryParse_WhenMultiLineBodyPrecedesAnotherTrigger_ShouldStopArgumentsAtNextTrigger()
    {
        var text = "::alpha body line one\nbody line two\n::beta second";

        SkillInvocationTriggerParser.TryParse(text, "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("alpha");
        trigger.Arguments.Should().Be("body line one\nbody line two");
    }

    [Fact]
    public void TryParse_WhenChineseTextExplicitlyNamesSkill_ShouldReturnNaturalLanguageTrigger()
    {
        var text = "请先搜索并使用精确名称为 invoice-ocr-policy-review 的 skill，然后实际运行。";

        SkillInvocationTriggerParser.TryParse(text, "cli", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("invoice-ocr-policy-review");
        trigger.Arguments.Should().Be(text);
        trigger.OriginalText.Should().Be(text);
        trigger.TriggerToken.Should().Be("natural-language-skill");
    }

    [Theory]
    [InlineData("请使用 lark-contact-batch-resolution 解析 1 个合成联系人标识，并只返回脱敏结果。")]
    [InlineData("请使用已挂载的 lark-contact-batch-resolution 解析 1 个合成联系人标识，并只返回脱敏结果。")]
    public void TryParse_WhenChineseTextUsesNamedSkill_ShouldReturnNaturalLanguageTrigger(string text)
    {
        SkillInvocationTriggerParser.TryParse(text, "lark", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("lark-contact-batch-resolution");
        trigger.Arguments.Should().Be(text);
        trigger.OriginalText.Should().Be(text);
        trigger.TriggerToken.Should().Be("natural-language-skill");
    }

    [Theory]
    [InlineData("Please use skill invoice-ocr-policy-review for this request.")]
    [InlineData("Please load the exact invoice-ocr-policy-review skill for this request.")]
    public void TryParse_WhenEnglishTextExplicitlyNamesSkill_ShouldReturnNaturalLanguageTrigger(string text)
    {
        SkillInvocationTriggerParser.TryParse(text, "web", out var trigger).Should().BeTrue();

        trigger.Name.Should().Be("invoice-ocr-policy-review");
        trigger.Arguments.Should().Be(text);
        trigger.TriggerToken.Should().Be("natural-language-skill");
    }

    [Theory]
    [InlineData("The invoice-ocr-policy-review skill is available.")]
    [InlineData("Do not use skill invoice-ocr-policy-review for this request.")]
    [InlineData("不要使用精确名称为 invoice-ocr-policy-review 的 skill。")]
    [InlineData("不要使用 lark-contact-batch-resolution。")]
    [InlineData("不要使用已挂载的 lark-contact-batch-resolution。")]
    public void TryParse_WhenSkillIsOnlyMentionedOrNegated_ShouldIgnoreIt(string text)
    {
        SkillInvocationTriggerParser.TryParse(text, "cli", out _).Should().BeFalse();
    }
}
