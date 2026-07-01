namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Read-only current system skill overlay accessor.</summary>
public interface ISystemSkillOverlayProvider
{
    Aevatar.AI.Abstractions.SystemSkillOverlay? GetCurrent();
}
