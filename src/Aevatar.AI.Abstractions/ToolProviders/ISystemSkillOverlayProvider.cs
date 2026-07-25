namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Read-only, context-aware accessor for the current system skill overlay. Both reply seams read
/// this single host-level source. <see cref="GetCurrent"/> is a synchronous cached read (never a
/// query-time fetch); staleness is refreshed in the background out of band.
/// </summary>
public interface ISystemSkillOverlayProvider
{
    Aevatar.AI.Abstractions.Prompting.GlobalSystemSkillPromptLayer? GetCurrent(SystemSkillOverlayRequest request);
}
