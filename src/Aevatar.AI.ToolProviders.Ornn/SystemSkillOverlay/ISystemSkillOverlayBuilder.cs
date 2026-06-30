namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

public interface ISystemSkillOverlayBuilder
{
    Task<Aevatar.AI.Abstractions.SystemSkillOverlay> BuildAsync(CancellationToken ct);
}
