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
    public async Task Start_ShouldPersistPromotedDecision_WithoutManagerSideChannel()
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

        publisher.Sent.Should().BeEmpty();
        agent.State.Completed.Should().BeFalse();

        await ExecutePendingAsync(agent, publisher);

        agent.State.ProposalId.Should().Be("proposal-1");
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        agent.State.DefinitionActorId.Should().Be("script-definition:script-1");
    }

    [Fact]
    public async Task Start_ShouldSendDirectDecisionResponse_WhenReplyStreamProvided()
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
            ProposalId = "proposal-direct",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "rollout",
            RequestId = "request-direct",
            ReplyStreamId = "reply-stream",
        });

        publisher.Sent.Should().BeEmpty();

        await ExecutePendingAsync(agent, publisher);

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("reply-stream");
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-direct");
        response.Found.Should().BeTrue();
        response.Accepted.Should().BeTrue();
        response.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        response.Diagnostics.Should().Contain("compile-ok");
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

        await ExecutePendingAsync(agent, publisher);
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
        await ExecutePendingAsync(agent, publisher);
        await agent.HandleStartScriptEvolutionSessionRequested(request);

        publisher.Sent.Should().BeEmpty();
        agent.State.ProposalId.Should().Be("proposal-duplicate");
    }

    [Fact]
    public async Task Start_ShouldReplayCompletedDecision_WhenSameProposalIdIsReplayedWithReplyStream()
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
        var request = new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-replay",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "duplicate",
            RequestId = "request-replay",
            ReplyStreamId = "reply-stream",
        };

        await agent.HandleStartScriptEvolutionSessionRequested(request);
        await ExecutePendingAsync(agent, publisher);
        publisher.Sent.Clear();
        await agent.HandleStartScriptEvolutionSessionRequested(request);

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-replay");
        response.Found.Should().BeTrue();
        response.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenSessionReceivesDifferentProposalAfterBound()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.PolicyRejected("policy-denied")),
            publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-bound",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
        });

        await ExecutePendingAsync(agent, publisher);
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

    [Fact]
    public async Task Start_ShouldQueueSelfExecutionInsteadOfCompletingInline()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            new FixedEvolutionFlowPort(
                ScriptEvolutionFlowResult.PolicyRejected("policy-denied")),
            publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-queued",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "rollout",
        });

        agent.State.Completed.Should().BeFalse();
        publisher.Published.Should().ContainSingle();
        publisher.Published[0].Direction.Should().Be(EventDirection.Self);
        publisher.Published[0].Payload.Should().BeOfType<ScriptEvolutionSessionExecutionRequestedEvent>();
    }

    private static async Task ExecutePendingAsync(
        ScriptEvolutionSessionGAgent agent,
        RecordingEventPublisher publisher)
    {
        var publishedCount = publisher.Published.Count;
        await agent.HandleScriptEvolutionSessionExecutionRequested(new ScriptEvolutionSessionExecutionRequestedEvent
        {
            ProposalId = agent.State.ProposalId,
        });

        var ready = await publisher.WaitForPlanReadyAsync(publishedCount + 1);
        await agent.HandleScriptEvolutionSessionExecutionPlanReady(ready);
    }

    private static ScriptEvolutionSessionGAgent CreateAgent(
        IScriptEvolutionFlowPort flowPort,
        RecordingEventPublisher publisher) =>
        new(new ScriptEvolutionExecutionCoordinator(flowPort, new StaticAddressResolver()), new StaticAddressResolver())
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
        public List<(EventDirection Direction, IMessage Payload)> Published { get; } = [];
        private readonly TaskCompletionSource<ScriptEvolutionSessionExecutionPlanReadyEvent> _nextPlanReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken ct = default) where TEvent : IMessage =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent eventData,
            EventDirection direction,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null) where TEvent : IMessage
        {
            Published.Add((direction, eventData));
            if (eventData is ScriptEvolutionSessionExecutionPlanReadyEvent ready)
                _nextPlanReady.TrySetResult(ready);
            return Task.CompletedTask;
        }

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

        public async Task<ScriptEvolutionSessionExecutionPlanReadyEvent> WaitForPlanReadyAsync(int minimumPublishedCount)
        {
            if (Published.Count >= minimumPublishedCount &&
                Published[^1].Payload is ScriptEvolutionSessionExecutionPlanReadyEvent ready)
            {
                return ready;
            }

            return await _nextPlanReady.Task;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => "script-definition:" + scriptId;

        public string GetEvolutionSessionActorId(string proposalId) => "script-evolution-session:" + proposalId;

        public string GetRuntimeActorId(string scriptId) => "script-runtime:" + scriptId;
    }
}
