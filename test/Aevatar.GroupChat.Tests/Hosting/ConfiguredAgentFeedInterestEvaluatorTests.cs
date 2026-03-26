using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Hosting.Configuration;
using Aevatar.GroupChat.Hosting.Feeds;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GroupChat.Tests.Hosting;

public sealed class ConfiguredAgentFeedInterestEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_ShouldAcceptDirectHintByDefault()
    {
        var evaluator = new ConfiguredAgentFeedInterestEvaluator(
            Options.Create(new GroupChatCapabilityOptions()),
            new Aevatar.GroupChat.Tests.Application.StubSourceRegistryQueryPort());

        var decision = await evaluator.EvaluateAsync(new GroupMentionHint
        {
            ParticipantAgentId = "agent-alpha",
            DirectHintAgentIds =
            {
                "agent-alpha",
            },
            TopicId = "topic-a",
            SenderId = "user-1",
        });

        decision.Should().NotBeNull();
        decision!.Score.Should().Be(100);
        decision.AcceptReason.Should().Be(GroupFeedAcceptReason.DirectHint);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldAcceptWhenTopicAndPublisherPushScoreOverThreshold()
    {
        var evaluator = new ConfiguredAgentFeedInterestEvaluator(
            Options.Create(new GroupChatCapabilityOptions
            {
                ParticipantInterestProfiles =
                {
                    new GroupChatParticipantInterestProfileOptions
                    {
                        ParticipantAgentId = "agent-beta",
                        MinimumInterestScore = 60,
                        DirectHintScore = 20,
                        TopicSubscriptionScore = 30,
                        PublisherSubscriptionScore = 20,
                        TopicIds = { "topic-a" },
                        PublisherAgentIds = { "agent-alpha" },
                    },
                },
            }),
            new Aevatar.GroupChat.Tests.Application.StubSourceRegistryQueryPort());

        var decision = await evaluator.EvaluateAsync(new GroupMentionHint
        {
            ParticipantAgentId = "agent-beta",
            DirectHintAgentIds =
            {
                "agent-beta",
            },
            TopicId = "topic-a",
            SenderKind = GroupMessageSenderKind.Agent,
            SenderId = "agent-alpha",
        });

        decision.Should().NotBeNull();
        decision!.Score.Should().Be(70);
        decision.AcceptReason.Should().Be(GroupFeedAcceptReason.DirectHint);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldRejectWhenScoreIsBelowThreshold()
    {
        var evaluator = new ConfiguredAgentFeedInterestEvaluator(
            Options.Create(new GroupChatCapabilityOptions
            {
                ParticipantInterestProfiles =
                {
                    new GroupChatParticipantInterestProfileOptions
                    {
                        ParticipantAgentId = "agent-beta",
                        MinimumInterestScore = 120,
                        DirectHintScore = 100,
                    },
                },
            }),
            new Aevatar.GroupChat.Tests.Application.StubSourceRegistryQueryPort());

        var decision = await evaluator.EvaluateAsync(new GroupMentionHint
        {
            ParticipantAgentId = "agent-beta",
            DirectHintAgentIds =
            {
                "agent-beta",
            },
        });

        decision.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldIncludeVerifiedSourceTrustScore()
    {
        var sourceQueryPort = new Aevatar.GroupChat.Tests.Application.StubSourceRegistryQueryPort();
        sourceQueryPort.Sources["doc-1"] = new Aevatar.GroupChat.Abstractions.Queries.GroupSourceCatalogSnapshot(
            "group-chat:source:doc-1",
            "doc-1",
            GroupSourceKind.Document,
            "doc://architecture/spec-1",
            GroupSourceAuthorityClass.InternalAuthoritative,
            GroupSourceVerificationStatus.Verified,
            2,
            "evt-2",
            DateTimeOffset.Parse("2026-03-25T00:00:00+00:00"));
        var evaluator = new ConfiguredAgentFeedInterestEvaluator(
            Options.Create(new GroupChatCapabilityOptions
            {
                ParticipantInterestProfiles =
                {
                    new GroupChatParticipantInterestProfileOptions
                    {
                        ParticipantAgentId = "agent-beta",
                        MinimumInterestScore = 145,
                        DirectHintScore = 100,
                    },
                },
            }),
            sourceQueryPort);

        var decision = await evaluator.EvaluateAsync(new GroupMentionHint
        {
            ParticipantAgentId = "agent-beta",
            DirectHintAgentIds =
            {
                "agent-beta",
            },
            SourceIds =
            {
                "doc-1",
            },
            EvidenceRefCount = 1,
        });

        decision.Should().NotBeNull();
        decision!.Score.Should().Be(145);
        decision.AcceptReason.Should().Be(GroupFeedAcceptReason.DirectHint);
    }
}
