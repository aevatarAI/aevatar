namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdChatPlanGateOptions
{
    public const string ConfigSection = "Aevatar:NyxId:PlanGate";
    public const int DefaultConfirmationThresholdSeconds = 10 * 60;

    public int ConfirmationThresholdSeconds { get; set; } = DefaultConfirmationThresholdSeconds;
}
