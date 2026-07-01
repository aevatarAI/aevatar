using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class SystemSkillOverlayPromptInjectionTests
{
    private const string OverlayMarkdown = "## Runtime system skills\n- prefer the host overlay";

    [Fact]
    public async Task DirectChat_ShouldAppendProviderOverlayForDmTurn()
    {
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedAgentAsync(provider, "role-overlay-direct");

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be($"kernel invariant\n\n{OverlayMarkdown}");

        // Direct chat is inherently a dm turn: the seam resolves the dm platform (global-scope members).
        provider.LastRequest.Platform.Should().Be(SystemSkillOverlayRequest.DirectChatPlatform);
    }

    [Fact]
    public async Task DirectChat_ShouldSkipEmptyProviderOverlay()
    {
        var agent = await CreateActivatedAgentAsync(new StubSystemSkillOverlayProvider("   "), "role-overlay-empty");

        agent.DecorateForTest("kernel invariant").Should().Be("kernel invariant");
    }

    [Fact]
    public async Task DirectChat_ShouldReturnKernelOnly_WhenNoProviderRegistered()
    {
        var agent = await CreateActivatedAgentAsync(overlayProvider: null, "role-overlay-none");

        agent.DecorateForTest("kernel invariant").Should().Be("kernel invariant");
    }

    [Fact]
    public async Task NyxIdChatGAgent_DirectChat_InjectsOverlayViaBaseDecoration()
    {
        // #2498 P1 regression guard: the real direct-chat actor (NyxIdChatGAgent) overrides
        // DecorateSystemPrompt. It must call base so RoleGAgent's overlay injection still runs —
        // otherwise direct chat silently loses the overlay (and its built-in fallback).
        var services = BuildServices(
            new InMemoryEventStoreForTests(),
            new StubSystemSkillOverlayProvider(OverlayMarkdown));
        var agent = new NyxIdChatGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, "nyxid-chat-overlay");
        await agent.ActivateAsync();

        var decorate = typeof(NyxIdChatGAgent).GetMethod(
            "DecorateSystemPrompt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        decorate.Should().NotBeNull();
        var prompt = (string)decorate!.Invoke(agent, ["kernel invariant"])!;

        prompt.Should().Contain(OverlayMarkdown);
    }

    [Fact]
    public async Task LegacyMaterializedEvent_ReplaysAsNoOp_WithoutAffectingPrompt()
    {
        // Retirement replay-safety (issue #2498): grains activated before the overlay moved host-level
        // may have a SystemSkillOverlayMaterializedEvent in their journal. The retired event type must
        // still deserialize and replay through its no-op reducer, leaving the overlay provider-sourced.
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedAgentAsync(provider, "role-overlay-legacy-replay");

        var replay = async () => await agent.PersistLegacyMaterializedEventAsync("## stale actor-state overlay");
        await replay.Should().NotThrowAsync();

        agent.DecorateForTest("kernel invariant")
            .Should()
            .Be($"kernel invariant\n\n{OverlayMarkdown}");
    }

    private static async Task<TestRoleGAgent> CreateActivatedAgentAsync(
        ISystemSkillOverlayProvider? overlayProvider,
        string actorId)
    {
        var services = BuildServices(new InMemoryEventStoreForTests(), overlayProvider);
        var agent = CreateAgent(services, actorId);
        await agent.ActivateAsync();
        return agent;
    }

    private static IServiceProvider BuildServices(IEventStore store, ISystemSkillOverlayProvider? overlayProvider)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoOpCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

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
        public Task PersistLegacyMaterializedEventAsync(string overlayMarkdown) =>
            PersistDomainEventAsync(new SystemSkillOverlayMaterializedEvent
            {
                Overlay = new SystemSkillOverlay
                {
                    OverlayMarkdown = overlayMarkdown,
                    SourceWatermark = "legacy-watermark",
                    MaterializedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });

        public string DecorateForTest(string basePrompt) => DecorateSystemPrompt(basePrompt);
    }

    private sealed class StubSystemSkillOverlayProvider(string overlayMarkdown) : ISystemSkillOverlayProvider
    {
        public SystemSkillOverlayRequest LastRequest { get; private set; }

        public SystemSkillOverlay GetCurrent(SystemSkillOverlayRequest request)
        {
            LastRequest = request;
            return new SystemSkillOverlay { OverlayMarkdown = overlayMarkdown, SourceWatermark = "test-watermark" };
        }
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
