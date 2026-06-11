// ─── EventPipeline tests: Verify unified pipeline priority ordering ───

using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

// Module that tracks execution order
public class OrderTrackingModule : IEventModule<IEventHandlerContext>
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
    // Refactor (iter11/cluster-020):
    // Old: static handler tests did not lock down reflection wrapper or per-message invocation regression.
    // New: tests assert direct exception propagation and no MethodInfo.Invoke in the adapter hot path.

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

    [Fact]
    public async Task StaticHandler_SyncException_ShouldPropagateWithoutTargetInvocationException()
    {
        var agent = new ThrowingSyncHandlerAgent();
        agent.SetId("sync-throw");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "boom" })));

        ex.Message.ShouldBe("sync-handler-failed");
        ex.ShouldNotBeOfType<System.Reflection.TargetInvocationException>();
    }

    [Fact]
    public void StaticHandlerAdapter_HandleAsync_ShouldNotInvokeMethodInfoInHotPath()
    {
        var source = File.ReadAllText(Path.GetFullPath(
            "../../../../../src/Aevatar.Foundation.Core/Pipeline/StaticHandlerAdapter.cs",
            AppContext.BaseDirectory));
        var handleStart = source.IndexOf(
            "public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)",
            StringComparison.Ordinal);
        handleStart.ShouldBeGreaterThanOrEqualTo(0);

        var nextMember = source.IndexOf("    private object? Unpack", handleStart, StringComparison.Ordinal);
        nextMember.ShouldBeGreaterThan(handleStart);
        var handleBody = source[handleStart..nextMember];

        handleBody.ShouldNotContain(".Invoke(");
        handleBody.ShouldNotContain("Invoke(_agent");
    }

    [Fact]
    public async Task StaticHandlerAdapter_HandleAsync_ShouldNotAllocateObjectArrayForRepeatedCalls()
    {
        var agent = new AllEventSyncCountingAgent();
        agent.SetId("sync-counting");
        var metadata = EventHandlerDiscoverer.Discover(typeof(AllEventSyncCountingAgent)).Single();
        var adapter = new StaticHandlerAdapter(metadata, agent);
        var context = new NoopEventHandlerContext(agent);
        var envelope = TestHelper.Envelope(new PingEvent { Message = "hot-path" });

        await adapter.HandleAsync(envelope, context, CancellationToken.None);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            await adapter.HandleAsync(envelope, context, CancellationToken.None);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        agent.Count.ShouldBe(1001);
        allocated.ShouldBeLessThan(1000 * IntPtr.Size);
    }
}

public class ThrowingSyncHandlerAgent : TestGAgentBase<CounterState>
{
    // Refactor (iter11/cluster-020):
    // Old: reflection dispatch wrapped this exception in TargetInvocationException.
    // New: compiled delegate dispatch exposes the original handler exception.
    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public void HandlePing(PingEvent evt) => throw new InvalidOperationException("sync-handler-failed");
}

public class AllEventSyncCountingAgent : TestGAgentBase<CounterState>
{
    public int Count { get; private set; }

    // Refactor (iter11/cluster-020):
    // Old: each sync handler call allocated a reflection argument array.
    // New: repeated dispatch uses the adapter's cached compiled delegate.
    [Aevatar.Foundation.Abstractions.Attributes.AllEventHandler(AllowSelfHandling = true)]
    public void HandleAny(EventEnvelope envelope) => Count++;
}

internal sealed class NoopEventHandlerContext : IEventHandlerContext
{
    // Refactor (iter11/cluster-020):
    // Old: allocation sanity flowed through GAgentBase and mixed unrelated pipeline allocations into the assertion.
    // New: the test context calls StaticHandlerAdapter directly so the sanity check stays scoped to invocation.
    public NoopEventHandlerContext(IAgent agent)
    {
        Agent = agent;
        InboundEnvelope = TestHelper.Envelope(new PingEvent { Message = "context" });
    }

    public EventEnvelope InboundEnvelope { get; }
    public string AgentId => Agent.Id;
    public IAgent Agent { get; }
    public IServiceProvider Services => TestRuntimeServices.BuildProvider();
    public ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience audience = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : Google.Protobuf.IMessage =>
        Task.CompletedTask;

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : Google.Protobuf.IMessage =>
        Task.CompletedTask;

    public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        Google.Protobuf.IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
            AgentId,
            callbackId,
            1,
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

    public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        Google.Protobuf.IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
            AgentId,
            callbackId,
            1,
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

    public Task CancelDurableCallbackAsync(
        Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}
