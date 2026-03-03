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
        var agent = new ScriptEvolutionSessionGAgent(new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                new InMemoryEventStore()),
        };

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
