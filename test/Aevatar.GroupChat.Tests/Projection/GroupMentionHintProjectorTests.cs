using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.Projectors;
using Aevatar.GroupChat.Tests.Application;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class GroupMentionHintProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldPublishOneHintPerDirectHintParticipant()
    {
        var publisher = new RecordingMentionHintPublisher();
        var projector = new GroupMentionHintProjector(publisher);
        var state = new GroupThreadState
        {
            GroupId = "group-a",
            ThreadId = "general",
            MessageEntries =
            {
                new GroupThreadMessageState
                {
                    MessageId = "msg-1",
                    TimelineCursor = 1,
                    SenderKind = GroupMessageSenderKind.User,
                    SenderId = "user-1",
                    Text = "@agent-alpha @agent-beta",
                    SourceRefs =
                    {
                        new GroupSourceRef
                        {
                            SourceKind = GroupSourceKind.Document,
                            Locator = "doc://architecture/spec-1",
                            SourceId = "doc-1",
                        },
                    },
                    EvidenceRefs =
                    {
                        new GroupEvidenceRef
                        {
                            EvidenceId = "evidence-1",
                            SourceLocator = "doc://architecture/spec-1",
                            Locator = "#L10",
                            ExcerptSummary = "important excerpt",
                            SourceId = "doc-1",
                        },
                    },
                    DirectHintAgentIds =
                    {
                        "agent-alpha",
                        "agent-beta",
                    },
                },
            },
        };

        await projector.ProjectAsync(
            new GroupTimelineProjectionContext
            {
                RootActorId = "group-chat:thread:group-a:general",
                ProjectionKind = "group-chat-timeline",
            },
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-user-message",
                        Version = 2,
                        EventData = Any.Pack(new UserMessagePostedEvent
                        {
                            GroupId = "group-a",
                            ThreadId = "general",
                            MessageId = "msg-1",
                            SenderUserId = "user-1",
                            Text = "@agent-alpha @agent-beta",
                            SourceRefs =
                            {
                                new GroupSourceRef
                                {
                                    SourceKind = GroupSourceKind.Document,
                                    Locator = "doc://architecture/spec-1",
                                    SourceId = "doc-1",
                                },
                            },
                            EvidenceRefs =
                            {
                                new GroupEvidenceRef
                                {
                                    EvidenceId = "evidence-1",
                                    SourceLocator = "doc://architecture/spec-1",
                                    Locator = "#L10",
                                    ExcerptSummary = "important excerpt",
                                    SourceId = "doc-1",
                                },
                            },
                            DirectHintAgentIds =
                            {
                                "agent-alpha",
                                "agent-beta",
                            },
                        }),
                    },
                    StateRoot = Any.Pack(state),
                }),
            });

        publisher.Hints.Should().HaveCount(2);
        publisher.Hints.Select(x => x.ParticipantAgentId).Should().Equal("agent-alpha", "agent-beta");
        publisher.Hints.All(x => x.SourceEventId == "evt-user-message").Should().BeTrue();
        publisher.Hints.All(x => x.TimelineCursor == 1).Should().BeTrue();
        publisher.Hints.All(x => x.DirectHintAgentIds.SequenceEqual(["agent-alpha", "agent-beta"])).Should().BeTrue();
        publisher.Hints.All(x => x.SenderKind == GroupMessageSenderKind.User).Should().BeTrue();
        publisher.Hints.All(x => x.SenderId == "user-1").Should().BeTrue();
        publisher.Hints.All(x => x.SourceIds.SequenceEqual(["doc-1"])).Should().BeTrue();
        publisher.Hints.All(x => x.SourceKinds.SequenceEqual([GroupSourceKind.Document])).Should().BeTrue();
        publisher.Hints.All(x => x.EvidenceRefCount == 1).Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEventsWithoutDirectHints()
    {
        var publisher = new RecordingMentionHintPublisher();
        var projector = new GroupMentionHintProjector(publisher);

        await projector.ProjectAsync(
            new GroupTimelineProjectionContext
            {
                RootActorId = "group-chat:thread:group-a:general",
                ProjectionKind = "group-chat-timeline",
            },
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-agent-message",
                        Version = 3,
                        EventData = Any.Pack(new AgentMessageAppendedEvent
                        {
                            GroupId = "group-a",
                            ThreadId = "general",
                            MessageId = "msg-2",
                            ParticipantAgentId = "agent-alpha",
                            Text = "done",
                        }),
                    },
                    StateRoot = Any.Pack(new GroupThreadState
                    {
                        GroupId = "group-a",
                        ThreadId = "general",
                    }),
                }),
            });

        publisher.Hints.Should().BeEmpty();
    }
}
