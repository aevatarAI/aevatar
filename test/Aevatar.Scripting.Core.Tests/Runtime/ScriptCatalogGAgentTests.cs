using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptCatalogGAgentTests
{
    [Fact]
    public async Task PromoteAndRollback_ShouldUpdateCatalogState()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
        });

        await agent.HandleRollbackScriptRevisionRequested(new RollbackScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "rollback-test",
            ProposalId = "proposal-3",
        });

        agent.State.Entries.Should().ContainKey("script-1");
        var entry = agent.State.Entries["script-1"];
        entry.ActiveRevision.Should().Be("rev-1");
        entry.PreviousRevision.Should().Be("rev-2");
        entry.ActiveDefinitionActorId.Should().BeEmpty();
        entry.ActiveSourceHash.Should().BeEmpty();
        entry.RevisionHistory.Should().Contain(new[] { "rev-1", "rev-2" });
    }

    [Fact]
    public async Task Promote_WithExpectedBaseRevision_ShouldRejectWhenActiveRevisionMismatch()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });

        var act = () => agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-0",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Promotion conflict*expected_base_revision=`rev-0`*actual_active_revision=`rev-1`*");
    }

    [Fact]
    public async Task Promote_WithExpectedBaseRevision_ShouldSucceedWhenActiveRevisionMatches()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-1",
        });

        agent.State.Entries.Should().ContainKey("script-1");
        agent.State.Entries["script-1"].ActiveRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task Promote_WithExpectedBaseRevision_ShouldAllowFirstPromotionWhenCatalogEntryMissing()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
            ExpectedBaseRevision = "rev-0",
        });

        agent.State.Entries.Should().ContainKey("script-1");
        agent.State.Entries["script-1"].ActiveRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task Rollback_WithExpectedCurrentRevisionMismatch_ShouldRejectAndKeepActiveRevision()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });
        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-2",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-1",
        });

        var act = () => agent.HandleRollbackScriptRevisionRequested(new RollbackScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "compensate promotion failure",
            ProposalId = "proposal-3",
            ExpectedCurrentRevision = "rev-3",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Rollback conflict*expected_current_revision=`rev-3`*actual_active_revision=`rev-2`*");

        agent.State.Entries.Should().ContainKey("script-1");
        agent.State.Entries["script-1"].ActiveRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task Rollback_WithExpectedCurrentRevisionMatch_ShouldSucceed()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });
        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-2",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-1",
        });

        await agent.HandleRollbackScriptRevisionRequested(new RollbackScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "compensate promotion failure",
            ProposalId = "proposal-3",
            ExpectedCurrentRevision = "rev-2",
        });

        agent.State.Entries.Should().ContainKey("script-1");
        var entry = agent.State.Entries["script-1"];
        entry.ActiveRevision.Should().Be("rev-1");
        entry.ActiveDefinitionActorId.Should().BeEmpty();
        entry.ActiveSourceHash.Should().BeEmpty();
    }

    [Fact]
    public async Task Rollback_ToCurrentActiveRevision_ShouldPreserveActiveMetadata()
    {
        var agent = new ScriptCatalogGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });
        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            Revision = "rev-2",
            DefinitionActorId = "definition-2",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-1",
        });

        await agent.HandleRollbackScriptRevisionRequested(new RollbackScriptRevisionRequestedEvent
        {
            ScriptId = "script-1",
            TargetRevision = "rev-2",
            Reason = "idempotent rollback",
            ProposalId = "proposal-3",
            ExpectedCurrentRevision = "rev-2",
        });

        agent.State.Entries.Should().ContainKey("script-1");
        var entry = agent.State.Entries["script-1"];
        entry.ActiveRevision.Should().Be("rev-2");
        entry.ActiveDefinitionActorId.Should().Be("definition-2");
        entry.ActiveSourceHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task QueryCatalogEntry_ShouldIgnore_WhenRequestOrReplyStreamMissing()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptCatalogGAgent
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptCatalogEntryRequested(new QueryScriptCatalogEntryRequestedEvent
        {
            RequestId = string.Empty,
            ReplyStreamId = "reply-stream",
            ScriptId = "script-1",
        });
        await agent.HandleQueryScriptCatalogEntryRequested(new QueryScriptCatalogEntryRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = string.Empty,
            ScriptId = "script-1",
        });

        publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryCatalogEntry_ShouldReturnNotFound_WhenScriptIdMissing()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptCatalogGAgent
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptCatalogEntryRequested(new QueryScriptCatalogEntryRequestedEvent
        {
            RequestId = "request-missing-script",
            ReplyStreamId = "reply-stream",
            ScriptId = string.Empty,
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptCatalogEntryRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-missing-script");
        response.Found.Should().BeFalse();
        response.FailureReason.Should().Contain("ScriptId is required");
    }

    [Fact]
    public async Task QueryCatalogEntry_ShouldReturnNotFound_WhenEntryMissing()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptCatalogGAgent
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptCatalogEntryRequested(new QueryScriptCatalogEntryRequestedEvent
        {
            RequestId = "request-missing-entry",
            ReplyStreamId = "reply-stream",
            ScriptId = "script-missing",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptCatalogEntryRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-missing-entry");
        response.Found.Should().BeFalse();
        response.ScriptId.Should().Be("script-missing");
        response.FailureReason.Should().Contain("not found");
    }

    [Fact]
    public async Task QueryCatalogEntry_ShouldReturnSnapshot_WhenEntryExists()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptCatalogGAgent
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptCatalogState>(
                new InMemoryEventStore()),
        };

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-query",
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
            SourceHash = "hash-1",
            ProposalId = "proposal-1",
        });

        await agent.HandlePromoteScriptRevisionRequested(new PromoteScriptRevisionRequestedEvent
        {
            ScriptId = "script-query",
            Revision = "rev-2",
            DefinitionActorId = "definition-2",
            SourceHash = "hash-2",
            ProposalId = "proposal-2",
            ExpectedBaseRevision = "rev-1",
        });

        await agent.HandleQueryScriptCatalogEntryRequested(new QueryScriptCatalogEntryRequestedEvent
        {
            RequestId = "request-hit",
            ReplyStreamId = "reply-stream",
            ScriptId = "script-query",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptCatalogEntryRespondedEvent>().Subject;
        response.Found.Should().BeTrue();
        response.ScriptId.Should().Be("script-query");
        response.ActiveRevision.Should().Be("rev-2");
        response.ActiveDefinitionActorId.Should().Be("definition-2");
        response.ActiveSourceHash.Should().Be("hash-2");
        response.PreviousRevision.Should().Be("rev-1");
        response.RevisionHistory.Should().Contain("rev-1");
        response.RevisionHistory.Should().Contain("rev-2");
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
