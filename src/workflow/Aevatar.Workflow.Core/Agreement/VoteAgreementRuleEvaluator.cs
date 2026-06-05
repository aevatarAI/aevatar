namespace Aevatar.Workflow.Core.Agreement;

internal sealed class VoteAgreementRuleEvaluator
{
    private readonly IVoteAgreementPredicateProvider _predicateProvider;

    public VoteAgreementRuleEvaluator(IVoteAgreementPredicateProvider predicateProvider)
    {
        _predicateProvider = predicateProvider;
    }

    public bool TryEvaluate(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule,
        out VoteAgreementDecision decision,
        out string error)
    {
        decision = new VoteAgreementDecision();
        error = string.Empty;

        if (candidates.Candidates.Count == 0)
        {
            error = "vote agreement requires at least one candidate";
            return false;
        }

        if (!VoteAgreementRuleConfigurationParser.ValidateRule(rule, out error))
            return false;

        decision = rule.Mode switch
        {
            AgreementRuleMode.All => EvaluateAll(candidates, rule),
            AgreementRuleMode.Majority => EvaluateMajority(candidates, rule),
            AgreementRuleMode.Quorum => EvaluateQuorum(candidates, rule),
            AgreementRuleMode.LabelCountConstraints => EvaluateLabelCountConstraints(candidates, rule),
            AgreementRuleMode.Predicate => EvaluatePredicate(candidates, rule, out error),
            _ => FailedDecision("unsupported agreement rule mode"),
        };

        return string.IsNullOrEmpty(error);
    }

    internal static AgreementVoteLabel ResolveCandidateLabel(
        VoteAgreementCandidate candidate,
        VoteAgreementRule rule)
    {
        var source = rule.LabelSource == AgreementCandidateLabelSource.Unspecified
            ? AgreementCandidateLabelSource.Success
            : rule.LabelSource;

        return source switch
        {
            AgreementCandidateLabelSource.Success => candidate.Success
                ? AgreementVoteLabel.Approve
                : AgreementVoteLabel.Reject,
            AgreementCandidateLabelSource.BranchKey => ParseLabel(candidate.BranchKey),
            AgreementCandidateLabelSource.Annotation => candidate.Annotations.TryGetValue(rule.LabelField, out var value)
                ? ParseLabel(value)
                : AgreementVoteLabel.Unspecified,
            _ => AgreementVoteLabel.Unspecified,
        };
    }

    private VoteAgreementDecision EvaluateAll(VoteAgreementCandidateSet candidates, VoteAgreementRule rule)
    {
        var counts = CountLabels(candidates, rule);
        var approveCount = CountOf(counts, AgreementVoteLabel.Approve);
        return CompleteDecision(
            approveCount == candidates.Candidates.Count ? AgreementDecisionKind.Agreed : AgreementDecisionKind.Rejected,
            candidates,
            rule,
            counts,
            approveCount == candidates.Candidates.Count
                ? "all candidates approved"
                : "at least one candidate did not approve");
    }

    private VoteAgreementDecision EvaluateMajority(VoteAgreementCandidateSet candidates, VoteAgreementRule rule)
    {
        var counts = CountLabels(candidates, rule);
        var approveCount = CountOf(counts, AgreementVoteLabel.Approve);
        var rejectCount = CountOf(counts, AgreementVoteLabel.Reject);
        var kind = approveCount > candidates.Candidates.Count / 2
            ? AgreementDecisionKind.Agreed
            : rejectCount > candidates.Candidates.Count / 2
                ? AgreementDecisionKind.Rejected
                : AgreementDecisionKind.Inconclusive;

        return CompleteDecision(kind, candidates, rule, counts, $"approve={approveCount}, reject={rejectCount}");
    }

    private VoteAgreementDecision EvaluateQuorum(VoteAgreementCandidateSet candidates, VoteAgreementRule rule)
    {
        var counts = CountLabels(candidates, rule);
        var approveCount = CountOf(counts, AgreementVoteLabel.Approve);
        var threshold = ResolveQuorumThreshold(candidates.Candidates.Count, rule);
        var kind = approveCount >= threshold
            ? AgreementDecisionKind.Agreed
            : AgreementDecisionKind.Rejected;

        return CompleteDecision(kind, candidates, rule, counts, $"approve={approveCount}, quorum={threshold}");
    }

    private VoteAgreementDecision EvaluateLabelCountConstraints(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule)
    {
        var counts = CountLabels(candidates, rule);
        var constraintsSatisfied = rule.CountConstraints.All(constraint =>
        {
            var count = CountOf(counts, constraint.Label);
            if (constraint.HasMinCount && count < constraint.MinCount)
                return false;
            if (constraint.HasMaxCount && count > constraint.MaxCount)
                return false;
            return true;
        });

        return CompleteDecision(
            constraintsSatisfied ? AgreementDecisionKind.Agreed : AgreementDecisionKind.Rejected,
            candidates,
            rule,
            counts,
            constraintsSatisfied ? "label count constraints satisfied" : "label count constraints failed");
    }

    private VoteAgreementDecision EvaluatePredicate(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule,
        out string error)
    {
        error = string.Empty;
        if (!_predicateProvider.TryEvaluate(rule.PredicateId, candidates, rule, out var result))
        {
            error = $"unknown vote agreement predicate '{rule.PredicateId}'";
            return new VoteAgreementDecision();
        }

        var counts = CountLabels(candidates, rule);
        var kind = result.IsAgreed ? AgreementDecisionKind.Agreed : AgreementDecisionKind.Rejected;
        return BuildDecision(
            kind,
            ResolveBranchKey(kind, rule),
            result.WinnerCandidateId ?? string.Empty,
            result.Output,
            result.Reason,
            counts);
    }

    private static VoteAgreementDecision CompleteDecision(
        AgreementDecisionKind kind,
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule,
        IReadOnlyDictionary<AgreementVoteLabel, int> counts,
        string reason)
    {
        var winner = kind == AgreementDecisionKind.Agreed
            ? ResolveWinner(candidates, rule)
            : null;

        return BuildDecision(
            kind,
            ResolveBranchKey(kind, rule),
            winner?.CandidateId ?? string.Empty,
            winner?.Output ?? string.Empty,
            reason,
            counts);
    }

    private static VoteAgreementCandidate? ResolveWinner(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule)
    {
        var policy = rule.WinnerPolicy == AgreementWinnerPolicy.Unspecified
            ? AgreementWinnerPolicy.FirstApproved
            : rule.WinnerPolicy;

        return policy switch
        {
            AgreementWinnerPolicy.First => candidates.Candidates.FirstOrDefault(),
            AgreementWinnerPolicy.FirstSuccess => candidates.Candidates.FirstOrDefault(x => x.Success),
            AgreementWinnerPolicy.FirstApproved => candidates.Candidates.FirstOrDefault(x =>
                ResolveCandidateLabel(x, rule) == AgreementVoteLabel.Approve),
            _ => candidates.Candidates.FirstOrDefault(),
        };
    }

    private static VoteAgreementDecision BuildDecision(
        AgreementDecisionKind kind,
        string branchKey,
        string winnerCandidateId,
        string output,
        string reason,
        IReadOnlyDictionary<AgreementVoteLabel, int> counts)
    {
        var decision = new VoteAgreementDecision
        {
            Kind = kind,
            BranchKey = branchKey,
            WinnerCandidateId = winnerCandidateId,
            Output = output,
            Reason = reason,
        };
        foreach (var (label, count) in counts)
            decision.LabelCounts[LabelKey(label)] = count;
        return decision;
    }

    private static VoteAgreementDecision FailedDecision(string reason) =>
        new()
        {
            Kind = AgreementDecisionKind.Rejected,
            BranchKey = "rejected",
            Reason = reason,
        };

    private static Dictionary<AgreementVoteLabel, int> CountLabels(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule)
    {
        var counts = new Dictionary<AgreementVoteLabel, int>();
        foreach (var candidate in candidates.Candidates)
        {
            var label = ResolveCandidateLabel(candidate, rule);
            counts[label] = counts.GetValueOrDefault(label) + 1;
        }

        return counts;
    }

    private static int CountOf(IReadOnlyDictionary<AgreementVoteLabel, int> counts, AgreementVoteLabel label) =>
        counts.GetValueOrDefault(label);

    private static int ResolveQuorumThreshold(int total, VoteAgreementRule rule)
    {
        if (rule.HasQuorumCount)
            return rule.QuorumCount;

        if (rule.HasQuorumRatio)
            return Math.Max(1, (int)Math.Ceiling(total * rule.QuorumRatio));

        return total;
    }

    private static string ResolveBranchKey(AgreementDecisionKind kind, VoteAgreementRule rule) =>
        kind switch
        {
            AgreementDecisionKind.Agreed => string.IsNullOrWhiteSpace(rule.OnAgreed) ? "agreed" : rule.OnAgreed,
            AgreementDecisionKind.Rejected => string.IsNullOrWhiteSpace(rule.OnRejected) ? "rejected" : rule.OnRejected,
            AgreementDecisionKind.Inconclusive => string.IsNullOrWhiteSpace(rule.OnInconclusive) ? "inconclusive" : rule.OnInconclusive,
            _ => "inconclusive",
        };

    private static AgreementVoteLabel ParseLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AgreementVoteLabel.Unspecified;

        return value.Trim().ToLowerInvariant() switch
        {
            "approve" or "approved" or "agree" or "agreed" or "true" or "success" or "yes" =>
                AgreementVoteLabel.Approve,
            "reject" or "rejected" or "false" or "failure" or "failed" or "no" =>
                AgreementVoteLabel.Reject,
            "abstain" or "skip" or "neutral" =>
                AgreementVoteLabel.Abstain,
            _ => AgreementVoteLabel.Unspecified,
        };
    }

    private static string LabelKey(AgreementVoteLabel label) =>
        label.ToString().ToLowerInvariant();
}
