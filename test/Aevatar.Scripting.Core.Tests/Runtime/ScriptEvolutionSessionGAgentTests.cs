using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionSessionGAgentTests
{
    [Fact]
    public async Task Start_ShouldDispatchProposeToManagerActor()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionSessionGAgent(new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                new InMemoryEventStore()),
        };

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
        var propose = publisher.Sent[0].Payload.Should().BeOfType<ProposeScriptEvolutionRequestedEvent>().Subject;
        propose.ProposalId.Should().Be("proposal-1");
        propose.CallbackActorId.Should().Be(agent.Id);
        propose.CallbackRequestId.Should().Be("proposal-1");

        agent.State.ProposalId.Should().Be("proposal-1");
        agent.State.ScriptId.Should().Be("script-1");
        agent.State.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task Decision_ShouldPersistSessionCompletionWithoutDirectStreamPush()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-2",
            ScriptId = "script-2",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "rollout",
        });

        publisher.Sent.Clear();

        await agent.HandleScriptEvolutionDecisionResponded(new ScriptEvolutionDecisionRespondedEvent
        {
            RequestId = "proposal-2",
            Found = true,
            Accepted = true,
            ProposalId = "proposal-2",
            ScriptId = "script-2",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            Status = "promoted",
            FailureReason = string.Empty,
            DefinitionActorId = "script-definition:script-2",
            CatalogActorId = "script-catalog",
            Diagnostics = { "compile-ok" },
        });

        publisher.Sent.Should().BeEmpty();

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        agent.State.Status.Should().Be("promoted");
    }

    [Fact]
    public async Task Start_ShouldGenerateProposalId_WhenMissing()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = string.Empty,
            ScriptId = "script-auto-id",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
            CandidateSourceHash = "hash",
            Reason = "auto-id",
        });

        agent.State.ProposalId.Should().NotBeNullOrWhiteSpace();
        publisher.Sent.Should().ContainSingle();
        var propose = publisher.Sent[0].Payload.Should().BeOfType<ProposeScriptEvolutionRequestedEvent>().Subject;
        propose.ProposalId.Should().Be(agent.State.ProposalId);
    }

    [Fact]
    public async Task Start_ShouldNoOp_WhenSameProposalIdIsReplayed()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);
        var request = new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-duplicate",
            ScriptId = "script-duplicate",
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
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);

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
        publisher.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenScriptIdMissing()
    {
        var agent = CreateAgent(new RecordingEventPublisher());
        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-missing-script",
            ScriptId = string.Empty,
            CandidateRevision = "rev-2",
            CandidateSource = "source",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ScriptId is required*");
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenCandidateRevisionMissing()
    {
        var agent = CreateAgent(new RecordingEventPublisher());
        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-missing-revision",
            ScriptId = "script-1",
            CandidateRevision = string.Empty,
            CandidateSource = "source",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CandidateRevision is required*");
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenCandidateSourceMissing()
    {
        var agent = CreateAgent(new RecordingEventPublisher());
        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-missing-source",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            CandidateSource = string.Empty,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CandidateSource is required*");
    }

    [Fact]
    public async Task Decision_ShouldIgnore_WhenSessionNotStarted()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);

        await agent.HandleScriptEvolutionDecisionResponded(new ScriptEvolutionDecisionRespondedEvent
        {
            ProposalId = "proposal-not-started",
            Accepted = true,
            Status = "promoted",
        });

        publisher.Sent.Should().BeEmpty();
        agent.State.Completed.Should().BeFalse();
        agent.State.ProposalId.Should().BeEmpty();
    }

    [Fact]
    public async Task Decision_ShouldIgnore_WhenProposalIdDoesNotMatch()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);
        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-expected",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
        });

        await agent.HandleScriptEvolutionDecisionResponded(new ScriptEvolutionDecisionRespondedEvent
        {
            ProposalId = "proposal-other",
            Accepted = true,
            Status = "promoted",
        });

        agent.State.Completed.Should().BeFalse();
        agent.State.Status.Should().Be("session_started");
    }

    [Fact]
    public async Task Decision_ShouldIgnore_WhenSessionAlreadyCompleted()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(publisher);
        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-completed",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source",
        });

        await agent.HandleScriptEvolutionDecisionResponded(new ScriptEvolutionDecisionRespondedEvent
        {
            ProposalId = "proposal-completed",
            Accepted = true,
            Status = "promoted",
            Diagnostics = { "first" },
        });
        await agent.HandleScriptEvolutionDecisionResponded(new ScriptEvolutionDecisionRespondedEvent
        {
            ProposalId = "proposal-completed",
            Accepted = false,
            Status = "rejected",
            FailureReason = "should-be-ignored",
            Diagnostics = { "second" },
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        agent.State.Status.Should().Be("promoted");
        agent.State.FailureReason.Should().BeEmpty();
        agent.State.Diagnostics.Should().ContainSingle(x => x == "first");
    }

    private static ScriptEvolutionSessionGAgent CreateAgent(RecordingEventPublisher publisher)
    {
        return new ScriptEvolutionSessionGAgent(new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                new InMemoryEventStore()),
        };
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

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }
}
