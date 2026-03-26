namespace Aevatar.GroupChat.Hosting.Configuration;

public sealed class GroupChatCapabilityOptions
{
    public const string SectionName = "GroupChat";

    public bool EnableDemoReplyGeneration { get; set; }

    public List<string> ParticipantAgentIds { get; set; } = [];

    public List<GroupChatParticipantInterestProfileOptions> ParticipantInterestProfiles { get; set; } = [];

    public string DemoReplyPrefix { get; set; } = "demo-reply";
}

public sealed class GroupChatParticipantInterestProfileOptions
{
    public string ParticipantAgentId { get; set; } = string.Empty;

    public int MinimumInterestScore { get; set; } = 100;

    public int DirectHintScore { get; set; } = 100;

    public int TopicSubscriptionScore { get; set; } = 40;

    public int PublisherSubscriptionScore { get; set; } = 20;

    public int EvidencePresenceScore { get; set; } = 10;

    public int VerifiedSourceScore { get; set; } = 15;

    public int InternalAuthoritativeSourceScore { get; set; } = 20;

    public int ExternalAuthoritativeSourceScore { get; set; } = 15;

    public int CommunityCuratedSourceScore { get; set; } = 10;

    public int UntrustedSourcePenalty { get; set; } = 30;

    public int RejectedSourcePenalty { get; set; } = 40;

    public List<string> TopicIds { get; set; } = [];

    public List<string> PublisherAgentIds { get; set; } = [];
}
