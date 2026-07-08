using System.Text;

namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

/// <summary>One resolved overlay member for rendering: its display name/description and SKILL.md body.</summary>
public sealed record OverlaySkillEntry(string Name, string Description, string Body);

/// <summary>
/// Renders overlay members into the force-injected markdown block within a byte/skill budget. Shared
/// by every per-platform snapshot variant so the budget/catalog degradation stays identical across
/// platforms (no double implementation).
/// </summary>
public static class SystemSkillOverlayRenderer
{
    private const string Header = "## System Skill Overlay (force-injected)";

    /// <summary>
    /// Renders <paramref name="entries"/> full-body first, degrading over-budget entries to catalog
    /// lines and, if still over budget, to a catalog-only block. Returns empty when nothing fits.
    /// </summary>
    public static string Render(IReadOnlyList<OverlaySkillEntry> entries, int maxSkills, int maxBytes)
    {
        if (entries.Count == 0 || maxBytes <= 0)
            return string.Empty;

        var fullBodyCount = Math.Clamp(maxSkills, 0, entries.Count);
        var markdown = RenderWithFullBodies(entries, fullBodyCount);
        while (fullBodyCount > 0 && Utf8ByteCount(markdown) > maxBytes)
        {
            fullBodyCount--;
            markdown = RenderWithFullBodies(entries, fullBodyCount);
        }

        return Utf8ByteCount(markdown) <= maxBytes
            ? markdown
            : RenderCatalogWithinBudget(entries, maxBytes);
    }

    private static string RenderWithFullBodies(IReadOnlyList<OverlaySkillEntry> entries, int fullBodyCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);

        for (var i = 0; i < entries.Count; i++)
        {
            builder.AppendLine();
            builder.AppendLine(i < fullBodyCount ? entries[i].Body : BuildCatalogLine(entries[i]));
        }

        return builder.ToString().TrimEnd();
    }

    private static string RenderCatalogWithinBudget(IReadOnlyList<OverlaySkillEntry> entries, int maxBytes)
    {
        var builder = new StringBuilder(Header);
        if (Utf8ByteCount(builder.ToString()) > maxBytes)
            return string.Empty;

        foreach (var entry in entries)
        {
            var candidate = builder.ToString() + Environment.NewLine + Environment.NewLine + BuildCatalogLine(entry);
            if (Utf8ByteCount(candidate) > maxBytes)
                break;

            builder.Clear();
            builder.Append(candidate);
        }

        return builder.ToString();
    }

    private static string BuildCatalogLine(OverlaySkillEntry entry)
    {
        var description = string.IsNullOrWhiteSpace(entry.Description)
            ? "No description."
            : entry.Description.Trim();
        return $"- {entry.Name.Trim()}: {description}";
    }

    private static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
}
