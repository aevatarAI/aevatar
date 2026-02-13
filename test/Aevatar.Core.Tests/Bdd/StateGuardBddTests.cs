// BDD: State mutation guard behavior.

using FluentAssertions;

namespace Aevatar.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "StateGuard")]
public class StateGuardBddTests
{
    [Fact(DisplayName = "Given agent, state mutation inside handler should succeed")]
    public async Task ModifyStateInHandler()
    {
        var agent = new CounterAgent();
        agent.SetId("g-1");
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 3 }));
        agent.State.Count.Should().Be(3);
    }

    [Fact(DisplayName = "Given agent, replacing state reference outside handler should throw")]
    public void ReplaceStateOutsideHandler()
    {
        var agent = new CounterAgent();
        agent.SetId("g-2");
        var prop = typeof(GAgentBase<CounterState>).GetProperty("State");
        var setter = prop!.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            var act = () => setter.Invoke(agent, [new CounterState { Count = 999 }]);
            act.Should().Throw<System.Reflection.TargetInvocationException>()
                .WithInnerException<InvalidOperationException>();
        }
    }

    [Fact(DisplayName = "StateGuard scope should nest and restore correctly")]
    public void NestedScopes()
    {
        StateGuard.IsWritable.Should().BeFalse();
        using (StateGuard.BeginWriteScope())
        {
            StateGuard.IsWritable.Should().BeTrue();
            using (StateGuard.BeginWriteScope())
                StateGuard.IsWritable.Should().BeTrue();
            StateGuard.IsWritable.Should().BeTrue();
        }
        StateGuard.IsWritable.Should().BeFalse();
    }
}
