using Aevatar.Scripting.Abstractions.Definitions;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Definitions;

public class ScriptPromotionDecisionTests
{
    [Fact]
    public void Rejected_ShouldThrow_WhenProposalIsNull()
    {
        Action act = () => ScriptPromotionDecision.Rejected(null!, "failed");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Rejected_ShouldNormalizeNullFields_AndUseEmptyValidation_WhenNotProvided()
    {
        var proposal = new ScriptEvolutionProposal(
            ProposalId: null!,
            ScriptId: null!,
            BaseRevision: null!,
            CandidateRevision: null!,
            CandidateSource: "source",
            CandidateSourceHash: "hash",
            Reason: "reason");

        var decision = ScriptPromotionDecision.Rejected(proposal, null!, validation: null);

        decision.Accepted.Should().BeFalse();
        decision.ProposalId.Should().BeEmpty();
        decision.ScriptId.Should().BeEmpty();
        decision.BaseRevision.Should().BeEmpty();
        decision.CandidateRevision.Should().BeEmpty();
        decision.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        decision.FailureReason.Should().BeEmpty();
        decision.DefinitionActorId.Should().BeEmpty();
        decision.CatalogActorId.Should().BeEmpty();
        decision.ValidationReport.Should().BeSameAs(ScriptEvolutionValidationReport.Empty);
    }

    [Fact]
    public void Rejected_ShouldKeepProvidedValidationReport()
    {
        var proposal = new ScriptEvolutionProposal(
            ProposalId: "proposal-1",
            ScriptId: "script-1",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            CandidateSource: "source",
            CandidateSourceHash: "hash",
            Reason: "reason");
        var validation = new ScriptEvolutionValidationReport(false, ["compile-failed"]);

        var decision = ScriptPromotionDecision.Rejected(proposal, "denied", validation);

        decision.ProposalId.Should().Be("proposal-1");
        decision.ScriptId.Should().Be("script-1");
        decision.BaseRevision.Should().Be("rev-1");
        decision.CandidateRevision.Should().Be("rev-2");
        decision.FailureReason.Should().Be("denied");
        decision.ValidationReport.Should().BeSameAs(validation);
    }
}

public class ScriptEvolutionValidationReportTests
{
    [Fact]
    public void Empty_ShouldExposeStableDefaultValues()
    {
        var report = ScriptEvolutionValidationReport.Empty;

        report.IsSuccess.Should().BeFalse();
        report.Diagnostics.Should().ContainSingle("Validation was not executed.");
    }
}
