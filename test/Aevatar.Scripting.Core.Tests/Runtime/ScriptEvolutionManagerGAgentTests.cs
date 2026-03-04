using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;

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
        });

        agent.State.Proposals.Should().ContainKey("proposal-denied");
        var proposal = agent.State.Proposals["proposal-denied"];
        proposal.Status.Should().Be("rejected");
        proposal.FailureReason.Should().Contain("policy-denied");
        proposal.ValidationSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Propose_ShouldSendDecisionToCallbackActor_WhenCallbackProvided()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
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
            CallbackActorId = "script-evolution-session:proposal-1",
            CallbackRequestId = "session-request-1",
        });

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("script-evolution-session:proposal-1");
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("session-request-1");
        response.Accepted.Should().BeTrue();
        response.ProposalId.Should().Be("proposal-1");
    }

    [Fact]
    public async Task Propose_ShouldReturnPromotionFailedStatus_WhenFlowPromotionFailsAfterUpsert()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PromotionFailed(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                "Promotion failed after definition upsert.",
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-candidate",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-failed",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-failed",
            CallbackRequestId = "session-request-failed",
        });

        agent.State.Proposals.Should().ContainKey("proposal-failed");
        agent.State.Proposals["proposal-failed"].Status.Should().Be("promotion_failed");
        agent.State.Proposals["proposal-failed"].FailureReason.Should().Contain("Promotion failed");

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Status.Should().Be("promotion_failed");
        response.DefinitionActorId.Should().Be("definition-candidate");
        response.CatalogActorId.Should().Be("catalog-1");
        response.Accepted.Should().BeFalse();
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

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where T : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where T : IMessage
        {
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
