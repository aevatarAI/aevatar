using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core;
using FluentAssertions;

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
}
