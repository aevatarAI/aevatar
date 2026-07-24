namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

public sealed class AgentProfileIngressProofOptions
{
    public const string SectionName = "Aevatar:AgentProfiles:IngressProof";

    public string CurrentKeyId { get; set; } = string.Empty;

    public string CurrentPrivateKeyPkcs8 { get; set; } = string.Empty;

    public Dictionary<string, string> PublicKeys { get; set; } = new(StringComparer.Ordinal);
}
