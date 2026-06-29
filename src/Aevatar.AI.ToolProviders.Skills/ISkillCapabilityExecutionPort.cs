namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Host-owned execution boundary for skill-advertised capabilities.
/// </summary>
public interface ISkillCapabilityExecutionPort
{
    Task<string> ExecuteAsync(
        SkillCapabilityExecutionRequest request,
        CancellationToken ct = default);
}

public sealed record SkillCapabilityExecutionRequest
{
    public required SkillDefinition Skill { get; init; }

    public required SkillCapabilityDescriptor Capability { get; init; }

    public required string ArgumentsJson { get; init; }
}
