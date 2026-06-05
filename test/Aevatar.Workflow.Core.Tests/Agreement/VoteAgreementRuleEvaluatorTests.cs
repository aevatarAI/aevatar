using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Agreement;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Agreement;

public sealed class VoteAgreementRuleEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenAllCandidatesApprove_ShouldAgree()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", true, "A"),
                Candidate("b", true, "B")),
            new VoteAgreementRule { Mode = AgreementRuleMode.All });

        decision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        decision.BranchKey.Should().Be("agreed");
        decision.Output.Should().Be("A");
    }

    [Fact]
    public void Evaluate_WhenMajorityRejects_ShouldReject()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", false, "A"),
                Candidate("b", false, "B"),
                Candidate("c", true, "C")),
            new VoteAgreementRule { Mode = AgreementRuleMode.Majority });

        decision.Kind.Should().Be(AgreementDecisionKind.Rejected);
        decision.BranchKey.Should().Be("rejected");
        decision.LabelCounts["approve"].Should().Be(1);
        decision.LabelCounts["reject"].Should().Be(2);
    }

    [Fact]
    public void Evaluate_WhenNoMajority_ShouldBeInconclusive()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", true, "A"),
                Candidate("b", false, "B")),
            new VoteAgreementRule { Mode = AgreementRuleMode.Majority });

        decision.Kind.Should().Be(AgreementDecisionKind.Inconclusive);
        decision.BranchKey.Should().Be("inconclusive");
    }

    [Fact]
    public void Evaluate_WhenQuorumCountSatisfied_ShouldAgree()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", true, "A"),
                Candidate("b", true, "B"),
                Candidate("c", false, "C")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Quorum,
                QuorumCount = 2,
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        decision.Output.Should().Be("A");
    }

    [Fact]
    public void Evaluate_WhenQuorumRatioUnsatisfied_ShouldReject()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", true, "A"),
                Candidate("b", false, "B"),
                Candidate("c", false, "C")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Quorum,
                QuorumRatio = 0.67,
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Rejected);
    }

    [Fact]
    public void Evaluate_WhenLabelCountConstraintsSatisfied_ShouldAgree()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", true, "A"),
                Candidate("b", false, "B", branchKey: "approve"),
                Candidate("c", true, "C")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.LabelCountConstraints,
                LabelSource = AgreementCandidateLabelSource.BranchKey,
                CountConstraints =
                {
                    new AgreementCountConstraint { Label = AgreementVoteLabel.Approve, MinCount = 1 },
                    new AgreementCountConstraint { Label = AgreementVoteLabel.Reject, MaxCount = 0 },
                },
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        decision.Output.Should().Be("B");
    }

    [Fact]
    public void Evaluate_WhenWinnerPolicyFirstSuccess_ShouldSkipFailedApprovedCandidate()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("a", false, "A", branchKey: "approve"),
                Candidate("b", true, "B", branchKey: "approve")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.LabelCountConstraints,
                LabelSource = AgreementCandidateLabelSource.BranchKey,
                WinnerPolicy = AgreementWinnerPolicy.FirstSuccess,
                CountConstraints =
                {
                    new AgreementCountConstraint { Label = AgreementVoteLabel.Approve, MinCount = 1 },
                },
            });

        decision.WinnerCandidateId.Should().Be("b");
        decision.Output.Should().Be("B");
    }

    [Fact]
    public void Evaluate_WhenPredicateUnknown_ShouldFail()
    {
        var evaluator = new VoteAgreementRuleEvaluator(new DefaultVoteAgreementPredicateProvider());
        var ok = evaluator.TryEvaluate(
            Candidates(Candidate("a", true, "A")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Predicate,
                PredicateId = "missing",
            },
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("unknown vote agreement predicate");
    }

    [Fact]
    public void Evaluate_WhenPredicateProviderMatches_ShouldAgree()
    {
        var decision = Evaluate(
            Candidates(Candidate("a", true, ""), Candidate("b", true, "B")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Predicate,
                PredicateId = "non_empty_output",
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        decision.WinnerCandidateId.Should().Be("b");
        decision.Output.Should().Be("B");
    }

    [Fact]
    public void Evaluate_WhenExactLabelPredicateMatchesAnnotation_ShouldAgreeWithWinner()
    {
        var decision = Evaluate(
            Candidates(
                Candidate("rejector", true, "needs work", annotations: new Dictionary<string, string> { ["vote"] = "reject" }),
                Candidate("approver", true, "ship it", annotations: new Dictionary<string, string> { ["vote"] = "approve" })),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Predicate,
                LabelSource = AgreementCandidateLabelSource.Annotation,
                LabelField = "vote",
                PredicateId = "exact_label:approve",
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        decision.WinnerCandidateId.Should().Be("approver");
        decision.Output.Should().Be("ship it");
        decision.LabelCounts["approve"].Should().Be(1);
        decision.LabelCounts["reject"].Should().Be(1);
    }

    [Fact]
    public void Evaluate_WhenExactLabelPredicateDoesNotMatch_ShouldReject()
    {
        var decision = Evaluate(
            Candidates(Candidate("rejector", true, "needs work", branchKey: "reject")),
            new VoteAgreementRule
            {
                Mode = AgreementRuleMode.Predicate,
                LabelSource = AgreementCandidateLabelSource.BranchKey,
                PredicateId = "exact_label:approve",
            });

        decision.Kind.Should().Be(AgreementDecisionKind.Rejected);
        decision.WinnerCandidateId.Should().BeEmpty();
        decision.Output.Should().BeEmpty();
        decision.Reason.Should().Contain("no candidate matched label");
    }

    [Fact]
    public void Parse_WhenJsonCountConstraintsProvided_ShouldPopulateTypedConstraints()
    {
        var ok = VoteAgreementRuleConfigurationParser.TryParse(
            new Dictionary<string, string>
            {
                ["rule_mode"] = "label_count_constraints",
                ["count_constraints"] =
                    """[{"label":"approve","min_count":2},{"label":"reject","max_count":0}]""",
            },
            out var rule,
            out var error);

        ok.Should().BeTrue(error);
        rule.CountConstraints.Should().HaveCount(2);
        rule.CountConstraints[0].Label.Should().Be(AgreementVoteLabel.Approve);
        rule.CountConstraints[0].MinCount.Should().Be(2);
        rule.CountConstraints[1].Label.Should().Be(AgreementVoteLabel.Reject);
        rule.CountConstraints[1].MaxCount.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"label":"approve"}""", "JSON array")]
    [InlineData("""[{"label":"unknown","min_count":1}]""", "label approve")]
    [InlineData("""[{"label":"approve"}]""", "requires min_count or max_count")]
    public void Parse_WhenJsonCountConstraintsInvalid_ShouldReturnError(string countConstraints, string expectedError)
    {
        var ok = VoteAgreementRuleConfigurationParser.TryParse(
            new Dictionary<string, string>
            {
                ["rule_mode"] = "label_count_constraints",
                ["count_constraints"] = countConstraints,
            },
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain(expectedError);
    }

    private static VoteAgreementDecision Evaluate(
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule)
    {
        var evaluator = new VoteAgreementRuleEvaluator(new DefaultVoteAgreementPredicateProvider());
        var ok = evaluator.TryEvaluate(candidates, rule, out var decision, out var error);
        ok.Should().BeTrue(error);
        return decision;
    }

    private static VoteAgreementCandidateSet Candidates(params VoteAgreementCandidate[] candidates)
    {
        var set = new VoteAgreementCandidateSet();
        set.Candidates.Add(candidates);
        return set;
    }

    private static VoteAgreementCandidate Candidate(
        string id,
        bool success,
        string output,
        string branchKey = "",
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        var candidate = new VoteAgreementCandidate
        {
            CandidateId = id,
            Success = success,
            Output = output,
            WorkerId = id,
            BranchKey = branchKey,
        };
        if (annotations != null)
        {
            foreach (var (key, value) in annotations)
                candidate.Annotations[key] = value;
        }

        return candidate;
    }
}
