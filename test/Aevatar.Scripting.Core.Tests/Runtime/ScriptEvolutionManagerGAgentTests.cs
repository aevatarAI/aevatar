using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionManagerGAgentTests
{
    [Fact]
    public async Task ProposedEvent_ShouldCreateProposalAndLatestIndex()
    {
        var agent = CreateAgent();

        await agent.HandleScriptEvolutionProposed(new ScriptEvolutionProposedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSourceHash = "hash-v2",
            Reason = "rollout",
        });

        agent.State.Proposals.Should().ContainKey("proposal-1");
        agent.State.Proposals["proposal-1"].Status.Should().Be(ScriptEvolutionStatuses.Proposed);
        agent.State.Proposals["proposal-1"].CandidateSourceHash.Should().Be("hash-v2");
        agent.State.LatestProposalByScript["script-1"].Should().Be("proposal-1");
    }

    [Fact]
    public async Task ValidatedEvent_ShouldUpdateDiagnosticsAndStatus()
    {
        var agent = CreateAgent();
        await agent.HandleScriptEvolutionProposed(new ScriptEvolutionProposedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
        });

        await agent.HandleScriptEvolutionValidated(new ScriptEvolutionValidatedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            IsValid = false,
            Diagnostics = { "validation-failed" },
        });

        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.ValidationFailed);
        proposal.ValidationSucceeded.Should().BeFalse();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "validation-failed");
    }

    [Fact]
    public async Task BuildRequested_ShouldCreateProposal_WhenMirrorArrivesBeforeProposal()
    {
        var agent = CreateAgent();

        await agent.HandleScriptEvolutionBuildRequested(new ScriptEvolutionBuildRequestedEvent
        {
            ProposalId = "proposal-build",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
        });

        var proposal = agent.State.Proposals["proposal-build"];
        proposal.ProposalId.Should().Be("proposal-build");
        proposal.ScriptId.Should().Be("script-1");
        proposal.Status.Should().Be(ScriptEvolutionStatuses.BuildRequested);
    }

    [Fact]
    public async Task ValidatedEvent_ShouldMarkPolicyAllowed_WhenValidationSucceeds()
    {
        var agent = CreateAgent();

        await agent.HandleScriptEvolutionValidated(new ScriptEvolutionValidatedEvent
        {
            ProposalId = "proposal-valid",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            IsValid = true,
            Diagnostics = { "compile-ok" },
        });

        var proposal = agent.State.Proposals["proposal-valid"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.Validated);
        proposal.PolicyAllowed.Should().BeTrue();
        proposal.ValidationSucceeded.Should().BeTrue();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "compile-ok");
    }

    [Fact]
    public async Task RejectedEvent_ShouldPreservePromotionFailedStatus()
    {
        var agent = CreateAgent();
        await agent.HandleScriptEvolutionProposed(new ScriptEvolutionProposedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
        });

        await agent.HandleScriptEvolutionRejected(new ScriptEvolutionRejectedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            FailureReason = "promotion failed",
            Status = ScriptEvolutionStatuses.PromotionFailed,
        });

        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.PromotionFailed);
        proposal.FailureReason.Should().Be("promotion failed");
    }

    [Fact]
    public async Task RejectedEvent_ShouldDefaultToRejected_WhenStatusMissing()
    {
        var agent = CreateAgent();

        await agent.HandleScriptEvolutionRejected(new ScriptEvolutionRejectedEvent
        {
            ProposalId = "proposal-rejected",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            FailureReason = "policy failed",
            Status = string.Empty,
        });

        var proposal = agent.State.Proposals["proposal-rejected"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        proposal.FailureReason.Should().Be("policy failed");
    }

    [Fact]
    public async Task PromotedEvent_ShouldStorePromotedDefinitionAndRevision()
    {
        var agent = CreateAgent();
        await agent.HandleScriptEvolutionProposed(new ScriptEvolutionProposedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
        });

        await agent.HandleScriptEvolutionPromoted(new ScriptEvolutionPromotedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            DefinitionActorId = "definition-2",
            CatalogActorId = "catalog-1",
        });

        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        proposal.PromotedDefinitionActorId.Should().Be("definition-2");
        proposal.PromotedRevision.Should().Be("rev-2");
        proposal.FailureReason.Should().BeEmpty();
    }

    [Fact]
    public async Task RollbackEvents_ShouldTrackTargetRevision_AndClearFailure()
    {
        var agent = CreateAgent();

        await agent.HandleScriptEvolutionRejected(new ScriptEvolutionRejectedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            FailureReason = "promotion failed",
            Status = ScriptEvolutionStatuses.PromotionFailed,
        });

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "operator rollback",
        });

        var pendingRollback = agent.State.Proposals["proposal-rollback"];
        pendingRollback.Status.Should().Be(ScriptEvolutionStatuses.RollbackRequested);
        pendingRollback.PromotedRevision.Should().Be("rev-1");
        pendingRollback.FailureReason.Should().Be("operator rollback");

        await agent.HandleScriptEvolutionRolledBack(new ScriptEvolutionRolledBackEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            PreviousRevision = "rev-2",
            CatalogActorId = "script-catalog",
        });

        var proposal = agent.State.Proposals["proposal-rollback"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
        proposal.PromotedRevision.Should().Be("rev-1");
        proposal.FailureReason.Should().BeEmpty();
    }

    private static ScriptEvolutionManagerGAgent CreateAgent()
    {
        return new ScriptEvolutionManagerGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };
    }
}
