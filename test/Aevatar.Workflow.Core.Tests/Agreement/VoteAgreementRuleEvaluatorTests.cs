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
        string branchKey = "") =>
        new()
        {
            CandidateId = id,
            Success = success,
            Output = output,
            WorkerId = id,
            BranchKey = branchKey,
        };
}
