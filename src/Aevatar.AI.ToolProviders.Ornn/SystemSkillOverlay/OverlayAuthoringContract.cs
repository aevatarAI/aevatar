namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

public static class OverlayAuthoringContract
{
    public static bool Validate(OverlayFrontmatter? frontmatter) => IsValid(frontmatter);

    public static bool IsValid(OverlayFrontmatter? frontmatter)
    {
        if (frontmatter == null ||
            string.IsNullOrWhiteSpace(frontmatter.Title) ||
            string.IsNullOrWhiteSpace(frontmatter.Scope) ||
            string.IsNullOrWhiteSpace(frontmatter.Priority) ||
            string.IsNullOrWhiteSpace(frontmatter.MaxBytes) ||
            string.IsNullOrWhiteSpace(frontmatter.AppliesTo) ||
            string.IsNullOrWhiteSpace(frontmatter.NonOverride))
        {
            return false;
        }

        if (!int.TryParse(frontmatter.Priority, out _) ||
            !int.TryParse(frontmatter.MaxBytes, out _) ||
            !bool.TryParse(frontmatter.NonOverride, out _))
        {
            return false;
        }

        return frontmatter.AppliesTo.ToLowerInvariant() is "channel" or "dm" or "both";
    }
}
