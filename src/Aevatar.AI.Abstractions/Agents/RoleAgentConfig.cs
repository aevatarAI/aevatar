namespace Aevatar.AI.Abstractions.Agents;

public class RoleAgentConfig
{
    public string ProviderName { get; set; } = "deepseek";
    public string? Model { get; set; }
    public string SystemPrompt { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int MaxToolRounds { get; set; } = 10;
    public int MaxHistoryMessages { get; set; } = 100;
}
