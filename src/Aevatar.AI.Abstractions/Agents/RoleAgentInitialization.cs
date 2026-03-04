namespace Aevatar.AI.Abstractions.Agents;

public class RoleAgentInitialization
{
    public string ProviderName { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string SystemPrompt { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int MaxToolRounds { get; set; } = 10;
    public int MaxHistoryMessages { get; set; } = 100;
    public int StreamBufferCapacity { get; set; } = 256;
}
