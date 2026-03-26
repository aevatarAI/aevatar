using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Feeds;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.GroupChat.Hosting.Feeds;

public sealed class ConfiguredAgentFeedInterestEvaluator : IAgentFeedInterestEvaluator
{
    private readonly GroupChatCapabilityOptions _options;
    private readonly ISourceRegistryQueryPort _sourceQueryPort;

    public ConfiguredAgentFeedInterestEvaluator(
        IOptions<GroupChatCapabilityOptions> options,
        ISourceRegistryQueryPort sourceQueryPort)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _sourceQueryPort = sourceQueryPort ?? throw new ArgumentNullException(nameof(sourceQueryPort));
    }

    public async Task<AgentFeedInterestDecision?> EvaluateAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var profile = ResolveProfile(hint.ParticipantAgentId);
        var score = 0;
        var acceptReason = GroupFeedAcceptReason.Unspecified;

        if (hint.DirectHintAgentIds.Contains(hint.ParticipantAgentId))
        {
            score += Math.Max(0, profile.DirectHintScore);
            acceptReason = GroupFeedAcceptReason.DirectHint;
        }

        if (!string.IsNullOrWhiteSpace(hint.TopicId) &&
            profile.TopicIds.Contains(hint.TopicId, StringComparer.Ordinal))
        {
            score += Math.Max(0, profile.TopicSubscriptionScore);
            if (acceptReason == GroupFeedAcceptReason.Unspecified)
                acceptReason = GroupFeedAcceptReason.TopicSubscription;
        }

        if (!string.IsNullOrWhiteSpace(hint.SenderId) &&
            profile.PublisherAgentIds.Contains(hint.SenderId, StringComparer.Ordinal))
        {
            score += Math.Max(0, profile.PublisherSubscriptionScore);
            if (acceptReason == GroupFeedAcceptReason.Unspecified)
                acceptReason = GroupFeedAcceptReason.PublisherSubscription;
        }

        if (hint.EvidenceRefCount > 0)
            score += Math.Max(0, profile.EvidencePresenceScore);

        score += await ResolveSourceTrustScoreAsync(hint, profile, ct);

        return score >= Math.Max(0, profile.MinimumInterestScore) &&
               acceptReason != GroupFeedAcceptReason.Unspecified
            ? new AgentFeedInterestDecision(score, acceptReason)
            : null;
    }

    private async Task<int> ResolveSourceTrustScoreAsync(
        GroupMentionHint hint,
        GroupChatParticipantInterestProfileOptions profile,
        CancellationToken ct)
    {
        var sourceIds = hint.SourceIds
            .Select(static x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sourceIds.Count == 0)
            return 0;

        int? bestScore = null;
        foreach (var sourceId in sourceIds)
        {
            var snapshot = await _sourceQueryPort.GetSourceAsync(sourceId, ct);
            if (snapshot == null)
                continue;

            var score = ResolveAuthorityScore(snapshot, profile) + ResolveVerificationScore(snapshot, profile);
            bestScore = bestScore.HasValue ? Math.Max(bestScore.Value, score) : score;
        }

        return bestScore ?? 0;
    }

    private static int ResolveAuthorityScore(
        GroupSourceCatalogSnapshot snapshot,
        GroupChatParticipantInterestProfileOptions profile) =>
        snapshot.AuthorityClass switch
        {
            GroupSourceAuthorityClass.InternalAuthoritative => Math.Max(0, profile.InternalAuthoritativeSourceScore),
            GroupSourceAuthorityClass.ExternalAuthoritative => Math.Max(0, profile.ExternalAuthoritativeSourceScore),
            GroupSourceAuthorityClass.CommunityCurated => Math.Max(0, profile.CommunityCuratedSourceScore),
            GroupSourceAuthorityClass.Untrusted => -Math.Max(0, profile.UntrustedSourcePenalty),
            _ => 0,
        };

    private static int ResolveVerificationScore(
        GroupSourceCatalogSnapshot snapshot,
        GroupChatParticipantInterestProfileOptions profile) =>
        snapshot.VerificationStatus switch
        {
            GroupSourceVerificationStatus.Verified => Math.Max(0, profile.VerifiedSourceScore),
            GroupSourceVerificationStatus.Rejected => -Math.Max(0, profile.RejectedSourcePenalty),
            _ => 0,
        };

    private GroupChatParticipantInterestProfileOptions ResolveProfile(string participantAgentId)
    {
        var profile = _options.ParticipantInterestProfiles.FirstOrDefault(x =>
            string.Equals(x.ParticipantAgentId?.Trim(), participantAgentId, StringComparison.Ordinal));
        if (profile != null)
            return profile;

        return new GroupChatParticipantInterestProfileOptions
        {
            ParticipantAgentId = participantAgentId,
        };
    }
}
