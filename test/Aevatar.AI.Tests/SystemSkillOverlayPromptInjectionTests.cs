using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class SystemSkillOverlayPromptInjectionTests
{
    private const string OverlayMarkdown = "## Runtime system skills\n- prefer the host overlay";

    [Fact]
    public async Task NyxIdChatGAgent_DirectChat_InjectsProviderOverlayForDmTurn()
    {
        // The chartered direct-chat actor owns the direct seam (issue #2498): its
        // DecorateSystemPrompt resolves the host-level provider and appends the overlay
        // after the kernel (kernel > overlay > runtime facts).
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedNyxIdChatAgentAsync(provider, "nyxid-chat-overlay");

        var prompt = DecorateViaReflection(agent, "kernel invariant", turnCatalog: null);

        prompt.Should().Contain("built-in prompt floor");
        prompt.IndexOf("kernel invariant", StringComparison.Ordinal).Should().BeLessThan(
            prompt.IndexOf("built-in prompt floor", StringComparison.Ordinal));
        prompt.IndexOf("built-in prompt floor", StringComparison.Ordinal).Should().BeLessThan(
            prompt.IndexOf(OverlayMarkdown, StringComparison.Ordinal));
        // Direct chat is inherently a dm turn: the seam resolves the dm platform (global-scope members).
        provider.LastRequest.Platform.Should().Be(SystemSkillOverlayRequest.DirectChatPlatform);
    }

    [Fact]
    public async Task NyxIdChatGAgent_DirectChat_SkipsEmptyProviderOverlay()
    {
        var agent = await CreateActivatedNyxIdChatAgentAsync(
            new StubSystemSkillOverlayProvider("   "), "nyxid-chat-overlay-empty");

        DecorateViaReflection(agent, "kernel invariant", turnCatalog: null).Should().StartWith("kernel invariant");
        DecorateViaReflection(agent, "kernel invariant", turnCatalog: null).Should().NotContain(OverlayMarkdown);
    }

    [Fact]
    public async Task NyxIdChatGAgent_DirectChat_RetainsBuiltInFloor_WhenNoGlobalProviderRegistered()
    {
        var agent = await CreateActivatedNyxIdChatAgentAsync(overlayProvider: null, "nyxid-chat-overlay-none");

        var prompt = DecorateViaReflection(agent, "kernel invariant", turnCatalog: null);
        prompt.Should().Contain("kernel invariant");
        prompt.Should().Contain("built-in prompt floor");
    }

    [Fact]
    public async Task PlainRoleGAgent_DoesNotReceiveOverlay_EvenWithProviderRegistered()
    {
        // Non-channel isolation (#2586): the base RoleGAgent serves classifier/workflow subclasses,
        // so it must never resolve the overlay provider. A registered provider must not leak channel
        // capability how-to into an arbitrary RoleGAgent system prompt.
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedAgentAsync(provider, "role-overlay-isolated");

        agent.DecorateForTest("kernel invariant", turnCatalog: null).Should().Be("kernel invariant");
        provider.GetCurrentCalls.Should().Be(0, "the base role agent must not even consult the provider");
    }

    [Fact]
    public async Task ChatbotClassifierGAgent_DoesNotReceiveOverlay_EvenWithProviderRegistered()
    {
        // The concrete regression from #2586: the classifier lives in the same container as the
        // overlay provider registration. Its per-turn system prompt must stay classification-only —
        // no ~19KB channel/Lark how-to appended to every classification turn.
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var services = BuildServices(new InMemoryEventStoreForTests(), provider);
        var agent = new ChatbotClassifierGAgent(TestAgentToolExecutionPort.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, "classifier-overlay-isolated");
        await agent.ActivateAsync();

        var prompt = DecorateViaReflection(agent, "classifier kernel", turnCatalog: null);

        prompt.Should().Be("classifier kernel");
        provider.GetCurrentCalls.Should().Be(0);
    }

    [Fact]
    public async Task LegacyMaterializedEvent_ReplaysAsNoOp_WithoutAffectingPrompt()
    {
        // Retirement replay-safety (issue #2498): grains activated before the overlay moved host-level
        // may have a SystemSkillOverlayMaterializedEvent in their journal. The retired event type must
        // still deserialize and replay through its no-op reducer, leaving the prompt kernel-only.
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedAgentAsync(provider, "role-overlay-legacy-replay");

        var replay = async () => await agent.PersistLegacyMaterializedEventAsync("## stale actor-state overlay");
        await replay.Should().NotThrowAsync();

        agent.DecorateForTest("kernel invariant", turnCatalog: null).Should().Be("kernel invariant");
    }

    [Fact]
    public async Task QueuedRefreshTimeout_IsAbsorbedAsNoOp_AndSchedulesNothing()
    {
        // Retirement replay-safety (issue #2498): grains activated before the overlay moved host-level
        // may still have a durable refresh timeout queued. The retired handler must absorb it without
        // scheduling a follow-up refresh or disturbing the kernel-only prompt.
        var provider = new StubSystemSkillOverlayProvider(OverlayMarkdown);
        var agent = await CreateActivatedAgentAsync(provider, "role-overlay-legacy-timeout");

        var absorb = async () => await agent.HandleSystemSkillOverlayRefresh(
            new SystemSkillOverlayRefreshFiredEvent { Attempt = 3 });
        await absorb.Should().NotThrowAsync();

        agent.DecorateForTest("kernel invariant", turnCatalog: null).Should().Be("kernel invariant");
    }

    private static async Task<NyxIdChatGAgent> CreateActivatedNyxIdChatAgentAsync(
        ISystemSkillOverlayProvider? overlayProvider,
        string actorId)
    {
        var services = BuildServices(new InMemoryEventStoreForTests(), overlayProvider);
        var agent = new NyxIdChatGAgent(
            new StubBuiltInPromptFloorProvider(),
            TestAgentToolExecutionPort.Instance,
            overlayProvider)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        await agent.ActivateAsync();
        return agent;
    }

    private static string DecorateViaReflection(
        RoleGAgent agent,
        string basePrompt,
        AgentTurnToolCatalog? turnCatalog)
    {
        var decorate = agent.GetType().GetMethod(
            "DecorateSystemPrompt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        decorate.Should().NotBeNull();
        return (string)decorate!.Invoke(agent, [basePrompt, turnCatalog])!;
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
        public TestRoleGAgent()
            : base(TestAgentToolExecutionPort.Instance)
        {
        }

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

        public string DecorateForTest(string basePrompt, AgentTurnToolCatalog? turnCatalog) =>
            DecorateSystemPrompt(basePrompt, turnCatalog);
    }

    private sealed class StubSystemSkillOverlayProvider(string overlayMarkdown) : ISystemSkillOverlayProvider
    {
        public SystemSkillOverlayRequest LastRequest { get; private set; }

        public int GetCurrentCalls { get; private set; }

        public GlobalSystemSkillPromptLayer GetCurrent(SystemSkillOverlayRequest request)
        {
            GetCurrentCalls++;
            LastRequest = request;
            return new GlobalSystemSkillPromptLayer(
                overlayMarkdown,
                new GlobalSystemSkillPromptProvenance("test-watermark"),
                new PromptLayerBounds(32 * 1024, 8192));
        }
    }

    internal sealed class StubBuiltInPromptFloorProvider : IBuiltInPromptFloorProvider
    {
        public BuiltInPromptFloorLayer GetFloor() =>
            new("built-in prompt floor", new BuiltInPromptFloorProvenance("test-floor"));
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
