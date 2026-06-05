namespace Aevatar.Workflow.Core.Agreement;

internal sealed class DefaultVoteAgreementPredicateProvider : IVoteAgreementPredicateProvider
{
    public bool TryEvaluate(
        string predicateId,
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule,
        out VoteAgreementPredicateResult result)
    {
        var normalized = (predicateId ?? string.Empty).Trim();
        if (string.Equals(normalized, "non_empty_output", StringComparison.OrdinalIgnoreCase))
        {
            var winner = candidates.Candidates.FirstOrDefault(x => x.Success && !string.IsNullOrWhiteSpace(x.Output));
            result = winner == null
                ? new VoteAgreementPredicateResult(false, null, string.Empty, "no successful candidate had non-empty output")
                : new VoteAgreementPredicateResult(true, winner.CandidateId, winner.Output, "non_empty_output matched");
            return true;
        }

        const string exactLabelPrefix = "exact_label:";
        if (normalized.StartsWith(exactLabelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var expectedLabel = normalized[exactLabelPrefix.Length..].Trim();
            if (TryParseLabel(expectedLabel, out var label))
            {
                var labeled = candidates.Candidates.FirstOrDefault(candidate =>
                    VoteAgreementRuleEvaluator.ResolveCandidateLabel(candidate, rule) == label);
                result = labeled == null
                    ? new VoteAgreementPredicateResult(false, null, string.Empty, $"no candidate matched label {label}")
                    : new VoteAgreementPredicateResult(true, labeled.CandidateId, labeled.Output, $"candidate matched label {label}");
                return true;
            }
        }

        result = new VoteAgreementPredicateResult(false, null, string.Empty, string.Empty);
        return false;
    }

    private static bool TryParseLabel(string value, out AgreementVoteLabel label)
    {
        label = AgreementVoteLabel.Unspecified;
        return Enum.TryParse(value, ignoreCase: true, out label) &&
               label != AgreementVoteLabel.Unspecified;
    }
}
