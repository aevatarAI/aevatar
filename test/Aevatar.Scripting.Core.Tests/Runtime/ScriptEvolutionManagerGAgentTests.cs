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
    public async Task Propose_ShouldPromote_WhenFlowReturnsPromoted()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
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

        flowPort.ExecutedProposals.Should().ContainSingle();
        flowPort.ExecutedProposals[0].ProposalId.Should().Be("proposal-1");

        agent.State.Proposals.Should().ContainKey("proposal-1");
        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be("promoted");
        proposal.ValidationSucceeded.Should().BeTrue();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "compile-ok");
    }

    [Fact]
    public async Task Propose_ShouldReject_WhenFlowReturnsPolicyRejected()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));

        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
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
        proposal.ValidationSucceeded.Should().BeFalse();
    }

    private sealed class FakeEvolutionFlowPort(ScriptEvolutionFlowResult result) : IScriptEvolutionFlowPort
    {
        public List<ScriptEvolutionProposal> ExecutedProposals { get; } = [];

        public Task<ScriptEvolutionFlowResult> ExecuteAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecutedProposals.Add(proposal);
            return Task.FromResult(result);
        }

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }
}
