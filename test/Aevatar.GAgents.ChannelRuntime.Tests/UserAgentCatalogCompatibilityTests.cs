using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;

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

    private static Any CreateLegacyAny(string typeUrl, Google.Protobuf.IMessage message) =>
        new()
        {
            TypeUrl = typeUrl,
            Value = message.ToByteString(),
        };
}
