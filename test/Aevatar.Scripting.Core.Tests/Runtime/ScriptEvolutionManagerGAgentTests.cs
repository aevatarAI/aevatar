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
    public async Task Index_ShouldPersistProposalSessionActorIds_AndLatestProposalByScript()
    {
        var agent = CreateAgent(new RecordingFlowPort());

        await agent.HandleScriptEvolutionProposalIndexed(new ScriptEvolutionProposalIndexedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            SessionActorId = "script-evolution-session:proposal-1",
        });

        agent.State.ProposalSessionActorIds.Should().ContainKey("proposal-1");
        agent.State.ProposalSessionActorIds["proposal-1"].Should().Be("script-evolution-session:proposal-1");
        agent.State.LatestProposalByScript.Should().ContainKey("script-1");
        agent.State.LatestProposalByScript["script-1"].Should().Be("proposal-1");
    }

    [Fact]
    public async Task Rollback_ShouldCallFlowPort_AndStampState()
    {
        var flowPort = new RecordingFlowPort();
        var agent = CreateAgent(flowPort);

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            CatalogActorId = "script-catalog",
            Reason = "manual rollback",
        });

        flowPort.RollbackRequests.Should().ContainSingle();
        flowPort.RollbackRequests[0].ProposalId.Should().Be("proposal-rollback");
        flowPort.RollbackRequests[0].TargetRevision.Should().Be("rev-1");

        agent.State.LastEventId.Should().Be("proposal-rollback:rolled_back");
        agent.State.LastAppliedEventVersion.Should().Be(2);
    }

    private static ScriptEvolutionManagerGAgent CreateAgent(RecordingFlowPort flowPort) =>
        new(flowPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

    private sealed class RecordingFlowPort : IScriptEvolutionFlowPort
    {
        public List<ScriptRollbackRequest> RollbackRequests { get; } = [];

        public Task<ScriptEvolutionFlowResult> ExecuteAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(ScriptEvolutionFlowResult.PolicyRejected("not-used"));

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct)
        {
            RollbackRequests.Add(request);
            return Task.CompletedTask;
        }
    }
}
