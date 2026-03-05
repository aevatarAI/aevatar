namespace Sisyphus.Application.Models.Paper;

public sealed class PaperOptions
{
    public const string SectionName = "Sisyphus:Paper";

    /// <summary>Maximum seconds to wait for tectonic PDF compilation.</summary>
    public int CompileTimeoutSeconds { get; set; } = 300;
}
