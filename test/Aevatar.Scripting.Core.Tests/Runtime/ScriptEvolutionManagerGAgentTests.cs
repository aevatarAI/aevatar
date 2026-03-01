using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionManagerGAgentTests
{
    [Fact]
    public async Task Propose_ShouldPromote_WhenPolicyAndValidationPass()
    {
        var policyPort = new FakePolicyGatePort(ScriptPolicyGateDecision.Allow);
        var validationPort = new FakeValidationPort(new ScriptEvolutionValidationReport(true, ["compile-ok"]));
        var promotionPort = new FakePromotionPort();

        var agent = new ScriptEvolutionManagerGAgent(policyPort, validationPort, promotionPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog-1",
            RequestedByActorId = "runtime-1",
        });

        agent.State.Proposals.Should().ContainKey("proposal-1");
        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be("promoted");
        proposal.ValidationSucceeded.Should().BeTrue();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "compile-ok");

        var decision = agent.GetDecision("proposal-1");
        decision.Should().NotBeNull();
        decision!.Accepted.Should().BeTrue();
        decision.Status.Should().Be("promoted");

        promotionPort.Promotions.Should().ContainSingle();
        promotionPort.Promotions[0].CandidateRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task Propose_ShouldReject_WhenPolicyDenied()
    {
        var policyPort = new FakePolicyGatePort(ScriptPolicyGateDecision.Deny("policy-denied"));
        var validationPort = new FakeValidationPort(new ScriptEvolutionValidationReport(true, ["compile-ok"]));
        var promotionPort = new FakePromotionPort();

        var agent = new ScriptEvolutionManagerGAgent(policyPort, validationPort, promotionPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-denied",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog-1",
            RequestedByActorId = "runtime-1",
        });

        agent.State.Proposals.Should().ContainKey("proposal-denied");
        var proposal = agent.State.Proposals["proposal-denied"];
        proposal.Status.Should().Be("rejected");
        proposal.FailureReason.Should().Contain("policy-denied");

        promotionPort.Promotions.Should().BeEmpty();
    }

    private sealed class FakePolicyGatePort(ScriptPolicyGateDecision decision) : IScriptPolicyGatePort
    {
        public Task<ScriptPolicyGateDecision> EvaluateAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(decision);
        }
    }

    private sealed class FakeValidationPort(ScriptEvolutionValidationReport report) : IScriptValidationPipelinePort
    {
        public Task<ScriptEvolutionValidationReport> ValidateAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(report);
        }
    }

    private sealed class FakePromotionPort : IScriptPromotionPort
    {
        public List<ScriptPromotionRequest> Promotions { get; } = [];

        public Task<ScriptPromotionResult> PromoteAsync(ScriptPromotionRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Promotions.Add(request);
            return Task.FromResult(
                new ScriptPromotionResult(
                    DefinitionActorId: request.DefinitionActorId,
                    CatalogActorId: request.CatalogActorId,
                    PromotedRevision: request.CandidateRevision));
        }

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
