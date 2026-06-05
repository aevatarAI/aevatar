namespace Aevatar.GAgents.Authoring.Lark;

public sealed class ScheduledAgentCreatorOptions
{
    public const string DefaultOrnnServiceSlug = "ornn-api";

    public string OrnnServiceSlug { get; set; } = DefaultOrnnServiceSlug;
}
