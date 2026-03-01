using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptHostGAgentReplayContractTests
{
    [Fact]
    public async Task HandleRequestedEvent_ShouldPersistDomainEvent_AndMutateViaTransitionOnly()
    {
        var agent = new ScriptHostGAgent();
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptHostState>(
            new InMemoryEventStore());

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            InputJson = "{}",
            ScriptRevision = "r1",
        });

        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
        agent.State.StatePayloadJson.Should().Contain("result");
    }
}
