using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class AgentModuleLifecycleAndPipelineTests
{
    [Fact]
    public async Task RegisterModule_after_pipeline_materialized_should_invalidate_cache()
    {
        var agent = new EmptyAgent();
        agent.SetId("pipeline-cache-register");

        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "warmup" }));

        var module = new TrackingModule();
        agent.RegisterModule(module);

        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "second" }));

        module.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetModules_after_pipeline_materialized_should_invalidate_cache()
    {
        var agent = new EmptyAgent();
        agent.SetId("pipeline-cache-set");

        var first = new TrackingModule();
        agent.RegisterModule(first);
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "first" }));

        var replacement = new TrackingModule();
        agent.SetModules([replacement]);
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "second" }));

        first.InvocationCount.ShouldBe(1);
        replacement.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task Activate_and_deactivate_should_initialize_and_dispose_lifecycle_aware_modules()
    {
        var agent = new StatelessModuleAgent();
        agent.SetId("module-lifecycle");

        var module = new LifecycleTrackingModule();
        agent.RegisterModule(module);

        await agent.ActivateAsync();
        await agent.DeactivateAsync();

        module.InitializeCount.ShouldBe(1);
        module.DisposeCount.ShouldBe(1);
    }

    private sealed class TrackingModule : IEventModule<IEventHandlerContext>
    {
        public string Name => "tracking";

        public int Priority => 0;

        public int InvocationCount { get; private set; }

        public bool CanHandle(EventEnvelope envelope)
        {
            _ = envelope;
            return true;
        }

        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        {
            _ = envelope;
            _ = ctx;
            _ = ct;
            InvocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleTrackingModule : ILifecycleAwareEventModule
    {
        public string Name => "lifecycle";

        public int Priority => 0;

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool CanHandle(EventEnvelope envelope)
        {
            _ = envelope;
            return false;
        }

        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        {
            _ = envelope;
            _ = ctx;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct)
        {
            _ = ct;
            InitializeCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StatelessModuleAgent : GAgentBase
    {
        public StatelessModuleAgent()
        {
            Services = TestRuntimeServices.BuildProvider();
        }
    }
}
