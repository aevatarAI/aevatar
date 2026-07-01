namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>System skill overlay configuration.</summary>
public class SystemSkillOverlayOptions
{
    /// <summary>
    /// Non-secret name of the public, org-owned Ornn skillset that sources the overlay (issue #2498).
    /// Ownership is the trust anchor, so no organization service token secret is needed; the set is
    /// resolved to a stable guid once and read by that guid to resist same-name squatting.
    /// </summary>
    public string SetName { get; set; } = string.Empty;

    /// <summary>
    /// Minimum interval before the overlay provider should refresh the overlay from its source.
    /// </summary>
    public TimeSpan RefreshTtl { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of source skills the provider should include in one overlay.
    /// </summary>
    public int MaxSkills { get; set; }

    /// <summary>
    /// Maximum UTF-8 byte budget the provider should allow for one overlay.
    /// </summary>
    public int MaxBytes { get; set; }

    /// <summary>
    /// Enables host registration for the system skill overlay scaffold. Defaults to disabled.
    /// </summary>
    public bool Enabled { get; set; }
}
