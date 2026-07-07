using System.Text;
using Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SystemSkillOverlayRendererTests
{
    [Fact]
    public void Render_WithinBudget_KeepsFullBodies()
    {
        var entries = Entries(("alpha", "first skill", "ALPHA FULL BODY"), ("beta", "second skill", "BETA FULL BODY"));

        var markdown = SystemSkillOverlayRenderer.Render(entries, maxSkills: 32, maxBytes: 32 * 1024);

        markdown.Should().Contain("ALPHA FULL BODY").And.Contain("BETA FULL BODY");
    }

    [Fact]
    public void Render_OverBudget_DegradesFullBodiesToCatalogLines_AndStaysWithinMaxBytes()
    {
        var bigBody = new string('x', 2048);
        var entries = Entries(("alpha", "first skill", bigBody), ("beta", "second skill", bigBody));
        var maxBytes = 512;

        var markdown = SystemSkillOverlayRenderer.Render(entries, maxSkills: 32, maxBytes: maxBytes);

        Encoding.UTF8.GetByteCount(markdown).Should().BeLessThanOrEqualTo(maxBytes);
        markdown.Should().NotContain(bigBody);
        markdown.Should().Contain("- alpha: first skill", "over-budget entries must degrade to catalog lines, not vanish");
    }

    [Fact]
    public void Render_MaxSkillsClamp_RendersExcessEntriesAsCatalogLines()
    {
        var entries = Entries(
            ("alpha", "first skill", "ALPHA FULL BODY"),
            ("beta", "second skill", "BETA FULL BODY"),
            ("gamma", "third skill", "GAMMA FULL BODY"));

        var markdown = SystemSkillOverlayRenderer.Render(entries, maxSkills: 1, maxBytes: 32 * 1024);

        markdown.Should().Contain("ALPHA FULL BODY");
        markdown.Should().NotContain("BETA FULL BODY").And.NotContain("GAMMA FULL BODY");
        markdown.Should().Contain("- beta: second skill").And.Contain("- gamma: third skill");
    }

    [Fact]
    public void Render_BudgetTooSmallForCatalog_TruncatesCatalogWithinMaxBytes()
    {
        var entries = Entries(
            ("alpha", new string('a', 300), "BODY"),
            ("beta", new string('b', 300), "BODY"));
        var maxBytes = 420; // header + roughly one catalog line

        var markdown = SystemSkillOverlayRenderer.Render(entries, maxSkills: 0, maxBytes: maxBytes);

        Encoding.UTF8.GetByteCount(markdown).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public void Render_NoEntriesOrNoBudget_ReturnsEmpty()
    {
        SystemSkillOverlayRenderer.Render([], maxSkills: 32, maxBytes: 1024).Should().BeEmpty();
        SystemSkillOverlayRenderer.Render(
            Entries(("alpha", "first skill", "BODY")), maxSkills: 32, maxBytes: 0).Should().BeEmpty();
    }

    private static OverlaySkillEntry[] Entries(params (string Name, string Description, string Body)[] items) =>
        items.Select(static item => new OverlaySkillEntry(item.Name, item.Description, item.Body)).ToArray();
}
