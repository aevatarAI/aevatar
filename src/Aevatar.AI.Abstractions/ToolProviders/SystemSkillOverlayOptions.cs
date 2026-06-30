namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>System skill overlay configuration.</summary>
public class SystemSkillOverlayOptions
{
    /// <summary>
    /// Host-bound Ornn tag used to select the system skills that may be materialized into the
    /// persisted role-agent overlay.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Host-bound organization service token used by the materializer to read system skills.
    /// This is a secret and must be supplied by host configuration.
    /// </summary>
    public string OrgServiceToken { get; set; } = string.Empty;

    /// <summary>
    /// Minimum interval before the materializer should refresh the overlay from its source.
    /// </summary>
    public TimeSpan RefreshTtl { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of source skills the materializer should include in one overlay.
    /// </summary>
    public int MaxSkills { get; set; }

    /// <summary>
    /// Maximum UTF-8 byte budget the materializer should allow for one overlay.
    /// </summary>
    public int MaxBytes { get; set; }

    /// <summary>
    /// Enables host registration for the system skill overlay scaffold. Defaults to disabled.
    /// </summary>
    public bool Enabled { get; set; }
}
