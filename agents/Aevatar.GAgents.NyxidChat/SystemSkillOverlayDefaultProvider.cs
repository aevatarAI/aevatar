using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Supplies the deployment's built-in default System Skill Overlay from an embedded resource, so the
/// per-domain capability how-to the kernel deliberately no longer carries is still force-injected on
/// both reply seams. It also serves as the <see cref="ISystemSkillOverlayFallback"/> the Ornn-sourced
/// provider degrades to when its set is unreachable or empty — the no-regression floor. The default is
/// platform-agnostic (coarse), so it ignores the request's platform.
/// </summary>
public sealed class SystemSkillOverlayDefaultProvider : ISystemSkillOverlayProvider, ISystemSkillOverlayFallback
{
    private const string DefaultOverlayResourceSuffix = "system-skill-overlay-default.md";
    private const string DefaultOverlayWatermark = "builtin-default";

    private static readonly Lazy<Aevatar.AI.Abstractions.SystemSkillOverlay> Cached = new(Load);

    public Aevatar.AI.Abstractions.SystemSkillOverlay GetCurrent(SystemSkillOverlayRequest request) => Cached.Value;

    public Aevatar.AI.Abstractions.SystemSkillOverlay GetFallback() => Cached.Value;

    private static Aevatar.AI.Abstractions.SystemSkillOverlay Load()
    {
        var assembly = typeof(SystemSkillOverlayDefaultProvider).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(DefaultOverlayResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return new Aevatar.AI.Abstractions.SystemSkillOverlay();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new Aevatar.AI.Abstractions.SystemSkillOverlay();

        using var reader = new StreamReader(stream);
        var markdown = reader.ReadToEnd().Trim();
        if (markdown.Length == 0)
            return new Aevatar.AI.Abstractions.SystemSkillOverlay();

        return new Aevatar.AI.Abstractions.SystemSkillOverlay
        {
            OverlayMarkdown = markdown,
            SourceWatermark = DefaultOverlayWatermark,
        };
    }
}
