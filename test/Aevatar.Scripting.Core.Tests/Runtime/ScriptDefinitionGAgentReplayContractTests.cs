using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptDefinitionGAgentReplayContractTests
{
    [Fact]
    public async Task HandleUpsertRequested_ShouldPersistDefinitionEvent_AndMutateViaTransitionOnly()
    {
        var agent = new ScriptDefinitionGAgent();
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
            new InMemoryEventStore());

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = "return 1;",
            SourceHash = "hash-1",
        });

        agent.State.ScriptId.Should().Be("script-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.SourceText.Should().Be("return 1;");
        agent.State.SourceHash.Should().Be("hash-1");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }
}
