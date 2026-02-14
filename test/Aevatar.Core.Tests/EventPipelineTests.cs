// ─── EventPipeline tests: Verify unified pipeline priority ordering ───

using Aevatar.Foundation.Abstractions.EventModules;
using Shouldly;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tests;

// Module that tracks execution order
public class OrderTrackingModule : IEventModule
{
    private readonly List<string> _log;
    public string Name { get; }
    public int Priority { get; }
    public bool CanHandle(EventEnvelope envelope) => true;

    public OrderTrackingModule(string name, int priority, List<string> log)
    {
        Name = name;
        Priority = priority;
        _log = log;
    }

    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        _log.Add(Name);
        return Task.CompletedTask;
    }
}

public class EventPipelineTests
{

    [Fact]
    public async Task Pipeline_ModulesAndHandlers_InterleavedByPriority()
    {
        // CounterAgent's HandleIncrement has default Priority = 0
        // CounterAgent's HandleDecrement has Priority = 10
        // Insert module with Priority = 5
        var agent = new CounterAgent();
        agent.SetId("pipeline-test");

        var executionLog = new List<string>();

        // Module at priority 5, between two static handlers
        var module = new OrderTrackingModule("mid_module", 5, executionLog);
        agent.RegisterModule(module);

        // Send IncrementEvent, only HandleIncrement(p=0) and mid_module(p=5) will handle
        // HandleDecrement(p=10) doesn't match IncrementEvent
        var envelope = TestHelper.Envelope(new IncrementEvent { Amount = 1 });
        await agent.HandleEventAsync(envelope);

        agent.State.Count.ShouldBe(1);
        executionLog.ShouldContain("mid_module");
    }

    [Fact]
    public async Task Pipeline_NoModules_OnlyStaticHandlers()
    {
        var agent = new CounterAgent();
        agent.SetId("no-modules");

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 3 }));
        agent.State.Count.ShouldBe(3);

        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 1 }));
        agent.State.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Pipeline_OnlyModules_NoStaticHandlers()
    {
        var agent = new EmptyAgent();
        agent.SetId("modules-only");

        var module = new TestModule();
        agent.RegisterModule(module);

        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "hi" }));
        module.InvocationCount.ShouldBe(1);
    }
}

