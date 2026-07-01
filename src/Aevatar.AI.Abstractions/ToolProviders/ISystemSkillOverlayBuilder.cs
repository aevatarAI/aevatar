namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Builds the host-bound system skill overlay for role agents.</summary>
public interface ISystemSkillOverlayBuilder
{
    Task<Aevatar.AI.Abstractions.SystemSkillOverlay> BuildAsync(CancellationToken ct);
}
