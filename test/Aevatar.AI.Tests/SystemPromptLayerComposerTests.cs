using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Prompting;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class SystemPromptLayerComposerTests
{
    [Fact]
    public void Compose_IncludesAllTypedLayersInFixedOrderWithNamedReportsAndProvenance()
    {
        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel"),
            Floor("floor"),
            Global("global", "global-watermark"),
            new ProfileRoutingPromptLayer(
                "profile",
                new ProfileRoutingPromptProvenance("profile-source"),
                Bounds()),
            new SelectedSkillPromptLayer(
                "selected",
                new SelectedSkillPromptProvenance("selected-source"),
                Bounds()),
            new RuntimeFactsPromptLayer(
                "runtime",
                new RuntimeFactsPromptProvenance("runtime-source")),
            new ConversationContextPromptLayer(
                "conversation",
                new ConversationContextPromptProvenance("summary-source"),
                Bounds()));

        AssertAppearsInOrder(
            result.Prompt,
            "kernel",
            "floor",
            "global",
            "profile",
            "<selected-skill-procedure>\nselected\n</selected-skill-procedure>",
            "<untrusted-runtime-facts>\nruntime\n</untrusted-runtime-facts>",
            "<untrusted-conversation-summary>\nconversation\n</untrusted-conversation-summary>");
        result.Reports.Should().HaveCount(7).And.OnlyContain(report => report.Included);
        result.KernelProvenance.Source.Should().Be("kernel-source");
        result.BuiltInFloorProvenance.Source.Should().Be("floor-source");
        result.GlobalProvenance!.SourceWatermark.Should().Be("global-watermark");
        result.ProfileProvenance!.Source.Should().Be("profile-source");
        result.SelectedSkillProvenance!.Source.Should().Be("selected-source");
        result.RuntimeFactsProvenance!.Source.Should().Be("runtime-source");
        result.ConversationProvenance!.SummarySource.Should().Be("summary-source");
        result.Kernel.ActualUtf8Bytes.Should().Be(Encoding.UTF8.GetByteCount("kernel"));
        result.Kernel.EstimatedTokens.Should().Be(2);
    }

    [Fact]
    public void CreateForProfile_IncludesProfileInstructionsInPromptLayer()
    {
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "nyxid-chat-default",
            ProfileVersion = "v1",
            PolicyRevision = "dinner-date-mock-v2",
            Instructions = "For dinner booking requests, read the user's dining profile before starting the workflow.",
        };

        var catalog = AgentTurnToolCatalogFactory.CreateForProfile(
            profile,
            finalNames: [],
            selectedIntentId: null,
            candidateIntentId: "dinner_booking",
            selectedSkillPromptLayer: null,
            diagnostics: null,
            exactTools: []);

        catalog.ProfilePromptLayer.Should().NotBeNull();
        catalog.ProfilePromptLayer!.Content.Should().Contain("Instructions:");
        catalog.ProfilePromptLayer.Content.Should().Contain(profile.Instructions);
    }

    [Fact]
    public void Compose_ThrowsTypedException_WhenKernelOrFloorIsMissingEmptyOrOverBudget()
    {
        var validKernel = Kernel("kernel");
        var validFloor = Floor("floor");

        Action nullKernel = () => Compose(null!, validFloor);
        Action emptyKernel = () => Compose(Kernel("   "), validFloor);
        Action largeKernel = () => Compose(Kernel(new string('k', 16 * 1024 + 1)), validFloor);
        Action nullFloor = () => Compose(validKernel, null!);
        Action emptyFloor = () => Compose(validKernel, Floor("   "));
        Action largeFloor = () => Compose(validKernel, Floor(new string('f', 32 * 1024 + 1)));

        nullKernel.Should().Throw<PromptLayerCompositionException>();
        emptyKernel.Should().Throw<PromptLayerCompositionException>();
        largeKernel.Should().Throw<PromptLayerCompositionException>();
        nullFloor.Should().Throw<PromptLayerCompositionException>();
        emptyFloor.Should().Throw<PromptLayerCompositionException>();
        largeFloor.Should().Throw<PromptLayerCompositionException>();
    }

    [Theory]
    [InlineData(0, 1, "maxUtf8Bytes")]
    [InlineData(-1, 1, "maxUtf8Bytes")]
    [InlineData(1, 0, "maxEstimatedTokens")]
    [InlineData(1, -1, "maxEstimatedTokens")]
    public void PromptLayerBounds_RejectsNonPositiveLimits(
        int maxUtf8Bytes,
        int maxEstimatedTokens,
        string parameterName)
    {
        var construct = () => new PromptLayerBounds(maxUtf8Bytes, maxEstimatedTokens);

        construct.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public void Compose_RejectsOptionalLayer_WhenOnlyEstimatedTokenLimitIsExceeded()
    {
        const string rejectedContent = "12345";
        var global = new GlobalSystemSkillPromptLayer(
            rejectedContent,
            new GlobalSystemSkillPromptProvenance("global"),
            new PromptLayerBounds(maxUtf8Bytes: 5, maxEstimatedTokens: 1));

        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel"),
            Floor("floor"),
            global,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null);

        result.Global.Included.Should().BeFalse();
        result.Global.ActualUtf8Bytes.Should().Be(5);
        result.Global.EstimatedTokens.Should().Be(2);
        result.Global.Diagnostics.Should().ContainSingle().Which.Should().Be(
            new PromptLayerDiagnostic(
                PromptLayerDiagnosticCode.OptionalLayerRejectedOverBudget,
                "actual_utf8_bytes=5;max_utf8_bytes=5;estimated_tokens=2;max_estimated_tokens=1"));
        result.Prompt.Should().NotContain(rejectedContent);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("profile")]
    [InlineData("selected")]
    [InlineData("runtime")]
    [InlineData("conversation")]
    public void Compose_RejectsOnlyTheOverBudgetOptionalLayer(string rejectedSlot)
    {
        var rejectedContent = "REJECTED-CONTENT";
        var global = Global("global-ok");
        var profile = new ProfileRoutingPromptLayer(
            "profile-ok",
            new ProfileRoutingPromptProvenance("profile"),
            Bounds());
        var selected = new SelectedSkillPromptLayer(
            "selected-ok",
            new SelectedSkillPromptProvenance("selected"),
            Bounds());
        var runtime = new RuntimeFactsPromptLayer(
            "runtime-ok",
            new RuntimeFactsPromptProvenance("runtime"));
        var conversation = new ConversationContextPromptLayer(
            "conversation-ok",
            new ConversationContextPromptProvenance("summary"),
            Bounds());

        switch (rejectedSlot)
        {
            case "global":
                global = new GlobalSystemSkillPromptLayer(
                    rejectedContent,
                    new GlobalSystemSkillPromptProvenance("global"),
                    new PromptLayerBounds(1, 1));
                break;
            case "profile":
                profile = new ProfileRoutingPromptLayer(
                    rejectedContent,
                    new ProfileRoutingPromptProvenance("profile"),
                    new PromptLayerBounds(1, 1));
                break;
            case "selected":
                selected = new SelectedSkillPromptLayer(
                    rejectedContent,
                    new SelectedSkillPromptProvenance("selected"),
                    new PromptLayerBounds(1, 1));
                break;
            case "runtime":
                rejectedContent = new string('r', 16 * 1024 + 1);
                runtime = new RuntimeFactsPromptLayer(
                    rejectedContent,
                    new RuntimeFactsPromptProvenance("runtime"));
                break;
            case "conversation":
                conversation = new ConversationContextPromptLayer(
                    rejectedContent,
                    new ConversationContextPromptProvenance("summary"),
                    new PromptLayerBounds(1, 1));
                break;
        }

        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel"),
            Floor("floor"),
            global,
            profile,
            selected,
            runtime,
            conversation);
        var rejectedReport = rejectedSlot switch
        {
            "global" => result.Global,
            "profile" => result.Profile,
            "selected" => result.SelectedSkill,
            "runtime" => result.RuntimeFacts,
            _ => result.Conversation,
        };

        rejectedReport.Included.Should().BeFalse();
        rejectedReport.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == PromptLayerDiagnosticCode.OptionalLayerRejectedOverBudget);
        result.Reports.Where(report => !ReferenceEquals(report, rejectedReport))
            .Should().OnlyContain(report => report.Included);
        result.Prompt.Should().NotContain(rejectedContent);
    }

    [Fact]
    public void Compose_BoundsProviderOnlyDiagnosticsToFirstThreePlusTruncation()
    {
        var diagnostics = Diagnostics(5);
        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel", diagnostics),
            Floor("floor"),
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null);

        result.Kernel.Diagnostics.Select(diagnostic => diagnostic.Detail)
            .Should().Equal("provider-0", "provider-1", "provider-2", "omitted_count=2");
        result.Kernel.Diagnostics[^1].Code.Should().Be(PromptLayerDiagnosticCode.DiagnosticsTruncated);
    }

    [Fact]
    public void Compose_BoundsComposerAndProviderDiagnosticsUsingTheSameCandidateRule()
    {
        var rejected = new GlobalSystemSkillPromptLayer(
            "too-large",
            new GlobalSystemSkillPromptProvenance("global"),
            new PromptLayerBounds(1, 1),
            Diagnostics(4));

        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel"),
            Floor("floor"),
            rejected,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null);

        result.Global.Diagnostics.Should().HaveCount(4);
        result.Global.Diagnostics[0].Code.Should().Be(PromptLayerDiagnosticCode.OptionalLayerRejectedOverBudget);
        result.Global.Diagnostics[1].Detail.Should().Be("provider-0");
        result.Global.Diagnostics[2].Detail.Should().Be("provider-1");
        result.Global.Diagnostics[3].Should().Be(
            new PromptLayerDiagnostic(PromptLayerDiagnosticCode.DiagnosticsTruncated, "omitted_count=2"));
    }

    [Fact]
    public void Compose_KeepsExactlyFourDiagnosticsAndCapsTheSevenReportTotalAtTwentyEight()
    {
        var four = Diagnostics(4);
        var five = Diagnostics(5);
        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel", four),
            Floor("floor", five),
            new GlobalSystemSkillPromptLayer(
                "global",
                new GlobalSystemSkillPromptProvenance("global"),
                Bounds(),
                five),
            new ProfileRoutingPromptLayer("profile", new ProfileRoutingPromptProvenance("profile"), Bounds(), five),
            new SelectedSkillPromptLayer("selected", new SelectedSkillPromptProvenance("selected"), Bounds(), five),
            new RuntimeFactsPromptLayer("runtime", new RuntimeFactsPromptProvenance("runtime"), five),
            new ConversationContextPromptLayer(
                "conversation",
                new ConversationContextPromptProvenance("summary"),
                Bounds(),
                five));

        result.Kernel.Diagnostics.Should().Equal(four);
        result.Kernel.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == PromptLayerDiagnosticCode.DiagnosticsTruncated);
        result.Diagnostics.Should().HaveCount(28);
        result.Reports.Should().OnlyContain(report => report.Diagnostics.Count == 4);
    }

    [Fact]
    public void Compose_TruncatesDiagnosticDetailAtRuneBoundaryWithoutInvalidUtf8()
    {
        var detail = string.Concat(Enumerable.Repeat("😀", 100));
        var result = SystemPromptLayerComposer.Compose(
            Kernel("kernel", [new PromptLayerDiagnostic(PromptLayerDiagnosticCode.ProviderReported, detail)]),
            Floor("floor"),
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null);

        var bounded = result.Kernel.Diagnostics.Single().Detail;
        Encoding.UTF8.GetByteCount(bounded).Should().Be(256);
        bounded.EnumerateRunes().Should().HaveCount(64);
        bounded.Should().NotContain("�");
    }

    [Fact]
    public void Compose_DoesNotReplaySelectedSkillWhenTheNextTurnOmitsTheLayer()
    {
        var first = SystemPromptLayerComposer.Compose(
            Kernel("kernel"),
            Floor("floor"),
            global: null,
            profile: null,
            new SelectedSkillPromptLayer(
                "one-turn-procedure",
                new SelectedSkillPromptProvenance("selected"),
                Bounds()),
            runtimeFacts: null,
            conversation: null);
        var second = Compose(Kernel("kernel"), Floor("floor"));

        first.Prompt.Should().Contain("one-turn-procedure");
        second.Prompt.Should().NotContain("one-turn-procedure");
        second.SelectedSkill.Included.Should().BeFalse();
    }

    private static SystemPromptCompositionResult Compose(
        KernelPromptLayer kernel,
        BuiltInPromptFloorLayer floor) =>
        SystemPromptLayerComposer.Compose(
            kernel,
            floor,
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null);

    private static KernelPromptLayer Kernel(
        string content,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null) =>
        new(content, new KernelPromptProvenance("kernel-source"), diagnostics);

    private static BuiltInPromptFloorLayer Floor(
        string content,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null) =>
        new(content, new BuiltInPromptFloorProvenance("floor-source"), diagnostics);

    private static GlobalSystemSkillPromptLayer Global(
        string content,
        string watermark = "global-source") =>
        new(content, new GlobalSystemSkillPromptProvenance(watermark), Bounds());

    private static PromptLayerBounds Bounds() => new(4096, 1024);

    private static IReadOnlyList<PromptLayerDiagnostic> Diagnostics(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new PromptLayerDiagnostic(
                PromptLayerDiagnosticCode.ProviderReported,
                $"provider-{index}"))
            .ToArray();

    private static void AssertAppearsInOrder(string prompt, params string[] values)
    {
        var previousIndex = -1;
        foreach (var value in values)
        {
            var currentIndex = prompt.IndexOf(value, StringComparison.Ordinal);
            currentIndex.Should().BeGreaterThan(previousIndex);
            previousIndex = currentIndex;
        }
    }
}
