namespace Aevatar.Workflow.Integration.AI;

public sealed class SkillBackedHumanInteractionPortOptions
{
    public string DeliveryCapability { get; set; } = "human_interaction.delivery";

    public string ResolutionCapability { get; set; } = "human_interaction.resolution_update";

    public string? DeliveryToolName { get; set; }

    public string? ResolutionToolName { get; set; }
}
