namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientEsAclOptions
{
    public const string SectionName = "ChannelIdentity:OAuthClient:ElasticsearchAcl";

    public bool GrantMatchesGrainEventStoreInternal { get; set; }

    public string? GrantDescription { get; set; }
}
