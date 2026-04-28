using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCompatibilityTests
{
    private const string LegacyProtoPrefix = "type.googleapis.com/aevatar.gagents.channelruntime." + "Agent" + "Registry";

    [Fact]
    public void TryUnpackState_ShouldAcceptLegacyStateTypeUrl()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-compat-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                },
            },
        };
        var envelope = new EventEnvelope
        {
            Id = "evt-compat-state",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-compat-state",
                    Version = 11,
                    EventData = Any.Pack(new Empty()),
                },
                StateRoot = CreateLegacyAny(LegacyProtoPrefix + "State", state),
            }),
        };

        var ok = CommittedStateEventEnvelope.TryUnpackState<UserAgentCatalogState>(
            envelope,
            out var published,
            out var stateEvent,
            out var unpacked);

        ok.Should().BeTrue();
        published.Should().NotBeNull();
        stateEvent.Should().NotBeNull();
        stateEvent!.Version.Should().Be(11);
        unpacked.Should().NotBeNull();
        unpacked!.Entries.Should().ContainSingle(x => x.AgentId == "agent-compat-1");
    }

    [Fact]
    public void StateTransitionMatcher_ShouldAcceptLegacyEventTypeUrl()
    {
        var legacyEvent = CreateLegacyAny(
            LegacyProtoPrefix + "UpsertedEvent",
            new UserAgentCatalogUpsertedEvent
            {
                Entry = new UserAgentCatalogEntry
                {
                    AgentId = "agent-compat-2",
                    AgentType = WorkflowAgentDefaults.AgentType,
                    TemplateName = WorkflowAgentDefaults.TemplateName,
                },
            });

        var next = StateTransitionMatcher
            .Match(new UserAgentCatalogState(), legacyEvent)
            .On<UserAgentCatalogUpsertedEvent>(
                static (current, evt) =>
                {
                    var updated = current.Clone();
                    updated.Entries.Add(evt.Entry.Clone());
                    return updated;
                })
            .OrCurrent();

        next.Entries.Should().ContainSingle(x =>
            x.AgentId == "agent-compat-2" &&
            x.TemplateName == WorkflowAgentDefaults.TemplateName);
    }

    [Fact]
    public async Task HandleEventAsync_ShouldDispatchLegacyTypeUrl_ToRenamedEventHandler()
    {
        var agent = new LegacyDispatchProbeAgent();
        var envelope = new EventEnvelope
        {
            Id = "evt-compat-dispatch",
            Payload = CreateLegacyAny(
                LegacyProtoPrefix + "UpsertedEvent",
                new UserAgentCatalogUpsertedEvent
                {
                    Entry = new UserAgentCatalogEntry
                    {
                        AgentId = "agent-compat-3",
                        AgentType = SkillRunnerDefaults.AgentType,
                        TemplateName = "daily_report",
                        Status = "running",
                    },
                }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("publisher-compat", TopologyAudience.Children),
        };

        await agent.HandleEventAsync(envelope);

        agent.HandleCount.Should().Be(1);
        agent.LastHandled.Should().NotBeNull();
        agent.LastHandled!.Entry.AgentId.Should().Be("agent-compat-3");
        agent.LastHandled.Entry.Status.Should().Be("running");
        agent.State.Entries.Should().ContainSingle(x =>
            x.AgentId == "agent-compat-3" &&
            x.TemplateName == "daily_report");
    }

    private static Any CreateLegacyAny(string typeUrl, Google.Protobuf.IMessage message) =>
        new()
        {
            TypeUrl = typeUrl,
            Value = message.ToByteString(),
        };

    private sealed class LegacyDispatchProbeAgent : GAgentBase<UserAgentCatalogState>
    {
        public LegacyDispatchProbeAgent()
        {
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
                .BuildServiceProvider();
        }

        public UserAgentCatalogUpsertedEvent? LastHandled { get; private set; }

        public int HandleCount { get; private set; }

        [EventHandler]
        public Task HandleAsync(UserAgentCatalogUpsertedEvent evt)
        {
            LastHandled = evt.Clone();
            HandleCount++;
            State.Entries.Add(evt.Entry.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
