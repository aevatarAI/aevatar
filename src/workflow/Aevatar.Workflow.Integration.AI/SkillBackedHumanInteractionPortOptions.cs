namespace Aevatar.Workflow.Integration.AI;

public sealed class SkillBackedHumanInteractionPortOptions
{
    public string DeliveryCapability { get; init; } = "human_interaction.delivery";

    public string ResolutionCapability { get; init; } = "human_interaction.resolution_update";

    public string? DeliveryToolName { get; init; }

    public string? ResolutionToolName { get; init; }
}
