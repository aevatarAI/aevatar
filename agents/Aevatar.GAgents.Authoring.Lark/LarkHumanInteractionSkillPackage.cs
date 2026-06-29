using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.GAgents.Authoring.Lark;

internal static class LarkHumanInteractionSkillPackage
{
    public const string SkillName = "lark-human-interaction";

    public static SkillDefinition Create() =>
        new()
        {
            Name = SkillName,
            Description = "Delivers workflow human interactions as Lark interactive cards.",
            Instructions = "Render typed workflow human interaction requests as Lark interactive cards and send them through the host-owned NyxID/Lark boundary.",
            Source = SkillSource.Local,
            IsModelInvocable = false,
            Capabilities =
            [
                new SkillCapabilityDescriptor
                {
                    Capability = LarkHumanInteractionSkillCapabilityExecutionPort.DeliveryCapability,
                    ToolName = "lark_human_interaction_delivery",
                    Description = "Deliver workflow human-interaction suspensions as Lark interactive cards. Capability: human_interaction.delivery.",
                    ParametersSchema = "{\"type\":\"object\"}",
                },
                new SkillCapabilityDescriptor
                {
                    Capability = LarkHumanInteractionSkillCapabilityExecutionPort.ResolutionCapability,
                    ToolName = "lark_human_interaction_resolution_update",
                    Description = "Deliver workflow human-approval resolution updates as Lark interactive cards. Capability: human_interaction.resolution_update.",
                    ParametersSchema = "{\"type\":\"object\"}",
                },
            ],
        };
}
