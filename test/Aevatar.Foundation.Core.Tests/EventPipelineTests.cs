// ─── EventPipeline tests: Verify unified pipeline priority ordering ───

using Shouldly;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public sealed class PriorityTrackingAgent : TestGAgentBase<CounterState>
{
    private readonly List<string> _log;

    public PriorityTrackingAgent(List<string> log)
    {
        _log = log;
    }

    [EventHandler(Priority = -10)]
    public Task HandleFirst(IncrementEvent evt)
    {
        State.Count += evt.Amount;
        _log.Add("first");
        return Task.CompletedTask;
    }

    [EventHandler(Priority = 10)]
    public Task HandleSecond(IncrementEvent evt)
    {
        _log.Add("second");
        return Task.CompletedTask;
    }
}

public class EventPipelineTests
{

    [Fact]
    public async Task Pipeline_StaticHandlers_RunByPriority()
    {
        var executionLog = new List<string>();
        var agent = new PriorityTrackingAgent(executionLog);
        agent.SetId("pipeline-test");
        var envelope = TestHelper.Envelope(new IncrementEvent { Amount = 1 });
        await agent.HandleEventAsync(envelope);

        agent.State.Count.ShouldBe(1);
        executionLog.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Pipeline_StaticOnly_OnlyStaticHandlers()
    {
        var agent = new CounterAgent();
        agent.SetId("no-modules");

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 3 }));
        agent.State.Count.ShouldBe(3);

        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 1 }));
        agent.State.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Pipeline_OnlyStaticHandlers_NoMatch_NoError()
    {
        var agent = new EmptyAgent();
        agent.SetId("no-handlers");

        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "hi" }));
    }
}
