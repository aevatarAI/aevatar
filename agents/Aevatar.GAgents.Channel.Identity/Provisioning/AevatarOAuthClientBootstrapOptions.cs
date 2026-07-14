namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientBootstrapOptions
{
    public const string SectionName = "ChannelIdentity:OAuthClient:Bootstrap";

    public bool Enabled { get; set; } = true;
}
