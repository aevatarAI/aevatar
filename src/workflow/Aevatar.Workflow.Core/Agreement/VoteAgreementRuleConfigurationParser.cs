using System.Globalization;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Agreement;

internal static class VoteAgreementRuleConfigurationParser
{
    private static readonly string[] RuleParameterKeys =
    [
        "rule_mode",
        "mode",
        "quorum_count",
        "quorum_ratio",
        "min_approve_count",
        "max_approve_count",
        "min_reject_count",
        "max_reject_count",
        "min_abstain_count",
        "max_abstain_count",
        "label_source",
        "label_field",
        "predicate_id",
        "predicate",
        "winner_policy",
        "on_agreed",
        "on_rejected",
        "on_inconclusive",
    ];

    public static bool TryResolveRule(
        VoteAgreementRule? typedRule,
        IReadOnlyDictionary<string, string> parameters,
        out VoteAgreementRule rule,
        out string error)
    {
        if (typedRule != null && typedRule.Mode != AgreementRuleMode.Unspecified)
        {
            rule = typedRule.Clone();
            return ValidateRule(rule, out error);
        }

        return TryParse(parameters, out rule, out error);
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out VoteAgreementRule rule,
        out string error)
    {
        rule = new VoteAgreementRule
        {
            Mode = AgreementRuleMode.Majority,
            LabelSource = AgreementCandidateLabelSource.Success,
            WinnerPolicy = AgreementWinnerPolicy.FirstApproved,
        };
        error = string.Empty;

        if (TryGet(parameters, "rule_mode", out var modeText) ||
            TryGet(parameters, "mode", out modeText))
        {
            if (!TryParseMode(modeText, out var mode))
            {
                error = $"unknown vote agreement rule mode '{modeText}'";
                return false;
            }

            rule.Mode = mode;
        }

        if (TryGet(parameters, "label_source", out var labelSourceText))
        {
            if (!TryParseLabelSource(labelSourceText, out var labelSource))
            {
                error = $"unknown vote agreement label source '{labelSourceText}'";
                return false;
            }

            rule.LabelSource = labelSource;
        }

        if (TryGet(parameters, "label_field", out var labelField))
            rule.LabelField = labelField;

        if (TryGet(parameters, "quorum_count", out var quorumCountText))
        {
            if (!int.TryParse(quorumCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quorumCount))
            {
                error = "quorum_count must be a positive integer";
                return false;
            }

            rule.QuorumCount = quorumCount;
        }

        if (TryGet(parameters, "quorum_ratio", out var quorumRatioText))
        {
            if (!double.TryParse(quorumRatioText, NumberStyles.Float, CultureInfo.InvariantCulture, out var quorumRatio))
            {
                error = "quorum_ratio must be a decimal number between 0 and 1";
                return false;
            }

            rule.QuorumRatio = quorumRatio;
        }

        if (TryGet(parameters, "predicate_id", out var predicateId) ||
            TryGet(parameters, "predicate", out predicateId))
        {
            rule.PredicateId = predicateId;
        }

        if (TryGet(parameters, "winner_policy", out var winnerPolicyText))
        {
            if (!TryParseWinnerPolicy(winnerPolicyText, out var winnerPolicy))
            {
                error = $"unknown vote agreement winner policy '{winnerPolicyText}'";
                return false;
            }

            rule.WinnerPolicy = winnerPolicy;
        }

        if (TryGet(parameters, "on_agreed", out var onAgreed))
            rule.OnAgreed = onAgreed;
        if (TryGet(parameters, "on_rejected", out var onRejected))
            rule.OnRejected = onRejected;
        if (TryGet(parameters, "on_inconclusive", out var onInconclusive))
            rule.OnInconclusive = onInconclusive;

        if (!TryAddCountConstraints(parameters, rule, out error))
            return false;

        return ValidateRule(rule, out error);
    }

    public static bool ValidateRule(VoteAgreementRule rule, out string error)
    {
        error = string.Empty;
        if (rule.Mode == AgreementRuleMode.Unspecified)
        {
            error = "vote agreement rule mode is required";
            return false;
        }

        if (!Enum.IsDefined(rule.Mode))
        {
            error = $"unknown vote agreement rule mode '{rule.Mode}'";
            return false;
        }

        if (rule.LabelSource == AgreementCandidateLabelSource.Unspecified)
            rule.LabelSource = AgreementCandidateLabelSource.Success;

        if (!Enum.IsDefined(rule.LabelSource))
        {
            error = $"unknown vote agreement label source '{rule.LabelSource}'";
            return false;
        }

        if (rule.LabelSource == AgreementCandidateLabelSource.Annotation &&
            string.IsNullOrWhiteSpace(rule.LabelField))
        {
            error = "label_field is required when label_source=annotation";
            return false;
        }

        if (rule.HasQuorumCount && rule.QuorumCount <= 0)
        {
            error = "quorum_count must be a positive integer";
            return false;
        }

        if (rule.HasQuorumRatio && (rule.QuorumRatio <= 0 || rule.QuorumRatio > 1))
        {
            error = "quorum_ratio must be greater than 0 and less than or equal to 1";
            return false;
        }

        if (rule.Mode == AgreementRuleMode.Quorum &&
            !rule.HasQuorumCount &&
            !rule.HasQuorumRatio)
        {
            error = "quorum mode requires quorum_count or quorum_ratio";
            return false;
        }

        if (rule.Mode == AgreementRuleMode.LabelCountConstraints &&
            rule.CountConstraints.Count == 0)
        {
            error = "label_count_constraints mode requires at least one count constraint";
            return false;
        }

        if (rule.Mode == AgreementRuleMode.Predicate &&
            string.IsNullOrWhiteSpace(rule.PredicateId))
        {
            error = "predicate mode requires predicate_id";
            return false;
        }

        if (rule.WinnerPolicy == AgreementWinnerPolicy.Unspecified)
            rule.WinnerPolicy = AgreementWinnerPolicy.FirstApproved;

        if (!Enum.IsDefined(rule.WinnerPolicy))
        {
            error = $"unknown vote agreement winner policy '{rule.WinnerPolicy}'";
            return false;
        }

        foreach (var constraint in rule.CountConstraints)
        {
            if (constraint.Label == AgreementVoteLabel.Unspecified || !Enum.IsDefined(constraint.Label))
            {
                error = "count constraint label must be approve, reject, or abstain";
                return false;
            }

            if (!constraint.HasMinCount && !constraint.HasMaxCount)
            {
                error = $"count constraint for {constraint.Label} requires min_count or max_count";
                return false;
            }

            if (constraint.HasMinCount && constraint.MinCount < 0)
            {
                error = "count constraint min_count must be non-negative";
                return false;
            }

            if (constraint.HasMaxCount && constraint.MaxCount < 0)
            {
                error = "count constraint max_count must be non-negative";
                return false;
            }

            if (constraint.HasMinCount &&
                constraint.HasMaxCount &&
                constraint.MinCount > constraint.MaxCount)
            {
                error = "count constraint min_count cannot be greater than max_count";
                return false;
            }
        }

        return true;
    }

    public static bool IsRuleParameterKey(string key) =>
        RuleParameterKeys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) ||
        key.StartsWith("min_", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_count", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("max_", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_count", StringComparison.OrdinalIgnoreCase);

    public static string? StripVoteParameterPrefix(string key) =>
        key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase)
            ? key["vote_param_".Length..]
            : null;

    private static bool TryAddCountConstraints(
        IReadOnlyDictionary<string, string> parameters,
        VoteAgreementRule rule,
        out string error)
    {
        error = string.Empty;
        foreach (var label in new[]
                 {
                     AgreementVoteLabel.Approve,
                     AgreementVoteLabel.Reject,
                     AgreementVoteLabel.Abstain,
                 })
        {
            var keyLabel = label.ToString().ToLowerInvariant();
            var constraint = new AgreementCountConstraint { Label = label };
            var hasConstraint = false;

            if (TryGet(parameters, $"min_{keyLabel}_count", out var minText))
            {
                if (!int.TryParse(minText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min))
                {
                    error = $"min_{keyLabel}_count must be a non-negative integer";
                    return false;
                }

                constraint.MinCount = min;
                hasConstraint = true;
            }

            if (TryGet(parameters, $"max_{keyLabel}_count", out var maxText))
            {
                if (!int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
                {
                    error = $"max_{keyLabel}_count must be a non-negative integer";
                    return false;
                }

                constraint.MaxCount = max;
                hasConstraint = true;
            }

            if (hasConstraint)
                rule.CountConstraints.Add(constraint);
        }

        if (TryGet(parameters, "count_constraints", out var constraintsJson) &&
            !string.IsNullOrWhiteSpace(constraintsJson))
        {
            if (!TryParseJsonCountConstraints(constraintsJson, rule, out error))
                return false;
        }

        return true;
    }

    private static bool TryParseJsonCountConstraints(
        string constraintsJson,
        VoteAgreementRule rule,
        out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(constraintsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "count_constraints must be a JSON array";
                return false;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("label", out var labelElement) ||
                    !TryParseVoteLabel(labelElement.GetString() ?? string.Empty, out var label))
                {
                    error = "count_constraints entries require label approve, reject, or abstain";
                    return false;
                }

                var constraint = new AgreementCountConstraint { Label = label };
                if (element.TryGetProperty("min_count", out var minElement))
                    constraint.MinCount = minElement.GetInt32();
                if (element.TryGetProperty("max_count", out var maxElement))
                    constraint.MaxCount = maxElement.GetInt32();

                rule.CountConstraints.Add(constraint);
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"count_constraints must be valid JSON: {ex.Message}";
            return false;
        }
        catch (FormatException ex)
        {
            error = $"count_constraints contains invalid count: {ex.Message}";
            return false;
        }
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        if (parameters.TryGetValue(key, out value!))
            return true;

        foreach (var (candidateKey, candidateValue) in parameters)
        {
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseMode(string value, out AgreementRuleMode mode)
    {
        mode = NormalizeToken(value) switch
        {
            "all" => AgreementRuleMode.All,
            "majority" => AgreementRuleMode.Majority,
            "quorum" => AgreementRuleMode.Quorum,
            "label_count_constraints" => AgreementRuleMode.LabelCountConstraints,
            "predicate" => AgreementRuleMode.Predicate,
            _ => AgreementRuleMode.Unspecified,
        };

        return mode != AgreementRuleMode.Unspecified;
    }

    private static bool TryParseLabelSource(string value, out AgreementCandidateLabelSource labelSource)
    {
        labelSource = NormalizeToken(value) switch
        {
            "success" => AgreementCandidateLabelSource.Success,
            "branch_key" => AgreementCandidateLabelSource.BranchKey,
            "annotation" => AgreementCandidateLabelSource.Annotation,
            _ => AgreementCandidateLabelSource.Unspecified,
        };

        return labelSource != AgreementCandidateLabelSource.Unspecified;
    }

    private static bool TryParseWinnerPolicy(string value, out AgreementWinnerPolicy winnerPolicy)
    {
        winnerPolicy = NormalizeToken(value) switch
        {
            "first_approved" => AgreementWinnerPolicy.FirstApproved,
            "first_success" => AgreementWinnerPolicy.FirstSuccess,
            "first" => AgreementWinnerPolicy.First,
            _ => AgreementWinnerPolicy.Unspecified,
        };

        return winnerPolicy != AgreementWinnerPolicy.Unspecified;
    }

    private static bool TryParseVoteLabel(string value, out AgreementVoteLabel label)
    {
        label = NormalizeToken(value) switch
        {
            "approve" or "approved" or "agree" or "agreed" => AgreementVoteLabel.Approve,
            "reject" or "rejected" => AgreementVoteLabel.Reject,
            "abstain" => AgreementVoteLabel.Abstain,
            _ => AgreementVoteLabel.Unspecified,
        };

        return label != AgreementVoteLabel.Unspecified;
    }

    private static string NormalizeToken(string value) =>
        (value ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
}
