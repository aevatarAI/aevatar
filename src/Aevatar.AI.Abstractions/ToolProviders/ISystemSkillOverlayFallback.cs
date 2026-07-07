namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// The deployment's built-in default overlay, used as the no-regression floor when the Ornn-sourced
/// set is unreachable or empty. Kept distinct from <see cref="ISystemSkillOverlayProvider"/> so the
/// Ornn-backed provider (infrastructure layer) can depend on the fallback without referencing the
/// agent-layer default implementation.
/// </summary>
public interface ISystemSkillOverlayFallback
{
    Aevatar.AI.Abstractions.SystemSkillOverlay? GetFallback();
}
