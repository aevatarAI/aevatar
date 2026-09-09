namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdAssistantActionsOptions
{
    public const string ConfigSection = "Aevatar:NyxId:AssistantActions";

    public bool Enabled { get; set; }

    public string? ScheduledDeliveryProviderSlug { get; set; }

    public string? ScheduledDeliveryProviderUserServiceId { get; set; }
}
