namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class NyxIdChatAgentProfileOptions
{
    public const string SectionName = "Aevatar:AgentProfiles:NyxIdChat";
    public const string StableProfileSlug = "nyxid-chat";

    public bool Enabled { get; set; }

    public string ReleaseSpecPath { get; set; } = string.Empty;
}
