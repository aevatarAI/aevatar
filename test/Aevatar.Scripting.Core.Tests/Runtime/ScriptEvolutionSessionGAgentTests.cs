using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionSessionGAgentTests
{
    [Fact]
    public async Task Start_ShouldPersistPromotedDecision_AndIndexManager()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.Promoted(
                    new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                    new ScriptPromotionResult(
                        DefinitionActorId: "script-definition:script-1",
                        CatalogActorId: "script-catalog",
                        PromotedRevision: "rev-2"))),
            publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "rollout",
        });

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("script-evolution-manager");
        publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionProposalIndexedEvent>();

        agent.State.ProposalId.Should().Be("proposal-1");
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        agent.State.DefinitionActorId.Should().Be("script-definition:script-1");
    }

    [Fact]
    public async Task QueryDecision_ShouldReturnCompletedDecision()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.ValidationFailed(
                    new ScriptEvolutionValidationReport(false, ["validation-failed"]))),
            publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-2",
            ScriptId = "script-2",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "validation",
        });

        publisher.Sent.Clear();

        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-2",
        });

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("reply-stream");
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Found.Should().BeTrue();
        response.Accepted.Should().BeFalse();
        response.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        response.Diagnostics.Should().Contain("validation-failed");
    }

    [Fact]
    public async Task Start_ShouldNoOp_WhenSameProposalIdIsReplayed()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.PolicyRejected("policy-denied")),
            publisher);
        var request = new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-duplicate",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "duplicate",
        };

        await agent.HandleStartScriptEvolutionSessionRequested(request);
        await agent.HandleStartScriptEvolutionSessionRequested(request);

        publisher.Sent.Should().ContainSingle();
        agent.State.ProposalId.Should().Be("proposal-duplicate");
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenSessionReceivesDifferentProposalAfterBound()
    {
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.PolicyRejected("policy-denied")),
            new RecordingEventPublisher());

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-bound",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
        });

        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-conflict",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to proposal*proposal-bound*proposal-conflict*");
    }

    private static ScriptEvolutionSessionGAgent CreateAgent(
        IScriptEvolutionFlowPort flowPort,
        RecordingEventPublisher publisher) =>
        new(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                new InMemoryEventStore()),
        };

    private sealed class FixedEvolutionFlowPort(ScriptEvolutionFlowResult result) : IScriptEvolutionFlowPort
    {
        public Task<ScriptEvolutionFlowResult> ExecuteAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(result);

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(string TargetActorId, IMessage Payload)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken ct = default) where TEvent : IMessage =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent eventData,
            EventDirection direction,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null) where TEvent : IMessage =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            string topic,
            TEvent eventData,
            CancellationToken ct = default) where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent eventData,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null) where TEvent : IMessage
        {
            Sent.Add((targetActorId, eventData));
            return Task.CompletedTask;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => "script-definition:" + scriptId;

        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => "script-evolution-session:" + proposalId;

        public string GetRuntimeActorId(string scriptId) => "script-runtime:" + scriptId;
    }
}
