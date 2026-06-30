using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class SystemSkillOverlayPromptInjectionTests
{
    private const string OverlayMarkdown = "## Runtime system skills\n- prefer the committed overlay";

    [Fact]
    public async Task DirectChat_ShouldAppendCommittedSystemSkillOverlay()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-overlay-direct");
        await agent.ActivateAsync();

        await agent.MaterializeOverlayAsync(OverlayMarkdown);

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be($"kernel invariant\n\n{OverlayMarkdown}");
    }

    [Fact]
    public async Task DirectChat_ShouldSkipEmptySystemSkillOverlay()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-overlay-empty");
        await agent.ActivateAsync();

        await agent.MaterializeOverlayAsync("   ");

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be("kernel invariant");
    }

    [Fact]
    public async Task DirectChat_ShouldFallBackToDefaultOverlay_WhenStateOverlayEmpty()
    {
        const string defaultOverlay = "## Built-in default overlay\n- provisioning how-to";
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store, new StubSystemSkillOverlayProvider(defaultOverlay));
        var agent = CreateAgent(services, "role-overlay-fallback");
        await agent.ActivateAsync();

        await agent.MaterializeOverlayAsync("   ");

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be($"kernel invariant\n\n{defaultOverlay}");
    }

    [Fact]
    public async Task DirectChat_ShouldPreferCommittedOverlayOverDefault()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store, new StubSystemSkillOverlayProvider("## default\n- fallback only"));
        var agent = CreateAgent(services, "role-overlay-prefer-committed");
        await agent.ActivateAsync();

        await agent.MaterializeOverlayAsync(OverlayMarkdown);

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be($"kernel invariant\n\n{OverlayMarkdown}");
    }

    private static IServiceProvider BuildServices(IEventStore store, ISystemSkillOverlayProvider? overlayProvider = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoOpCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<ISystemSkillOverlayBuilder, EmptyOverlayBuilder>()
            .AddSingleton(new SystemSkillOverlayOptions { Enabled = true });

        if (overlayProvider is not null)
            services.AddSingleton(overlayProvider);

        return services.BuildServiceProvider();
    }

    private static TestRoleGAgent CreateAgent(IServiceProvider services, string actorId)
    {
        var agent = new TestRoleGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static void AssignActorId(RoleGAgent agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed class TestRoleGAgent : RoleGAgent
    {
        public Task MaterializeOverlayAsync(string overlayMarkdown) =>
            PersistDomainEventAsync(new SystemSkillOverlayMaterializedEvent
            {
                Overlay = new SystemSkillOverlay
                {
                    OverlayMarkdown = overlayMarkdown,
                    SourceWatermark = "test-watermark",
                    MaterializedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });

        public string DecorateForTest(string basePrompt) => DecorateSystemPrompt(basePrompt);
    }

    private sealed class EmptyOverlayBuilder : ISystemSkillOverlayBuilder
    {
        public Task<SystemSkillOverlay> BuildAsync(CancellationToken ct) =>
            Task.FromResult(new SystemSkillOverlay());
    }

    private sealed class StubSystemSkillOverlayProvider(string overlayMarkdown) : ISystemSkillOverlayProvider
    {
        public SystemSkillOverlay GetCurrent() =>
            new() { OverlayMarkdown = overlayMarkdown, SourceWatermark = "builtin-default" };
    }

    private sealed class NoOpCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
