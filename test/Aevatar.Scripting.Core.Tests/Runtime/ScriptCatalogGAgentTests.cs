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
}
