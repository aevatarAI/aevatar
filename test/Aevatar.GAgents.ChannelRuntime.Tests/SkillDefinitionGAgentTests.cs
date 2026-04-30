using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillDefinitionGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private ServiceProvider _serviceProvider = null!;
    private SkillDefinitionGAgent _agent = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryEventStore();
        _serviceProvider = BuildServiceProvider(_store);
        _agent = CreateAgent("skill-runner-test");
        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenSamplingFieldsAreOmitted_ShouldKeepThemUnset()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillDefinitionInitializedEvent>();
        initialized.HasTemperature.Should().BeFalse();
        initialized.HasMaxTokens.Should().BeFalse();

        _agent.State.HasTemperature.Should().BeFalse();
        _agent.State.HasMaxTokens.Should().BeFalse();
        _agent.State.MaxToolRounds.Should().Be(SkillDefinitionDefaults.DefaultMaxToolRounds);
        _agent.State.MaxHistoryMessages.Should().Be(SkillDefinitionDefaults.DefaultMaxHistoryMessages);
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenTemperatureIsExplicitZero_ShouldPreserveIt()
    {
        var command = CreateInitializeCommand();
        command.Temperature = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillDefinitionInitializedEvent>();
        initialized.HasTemperature.Should().BeTrue();
        initialized.Temperature.Should().Be(0);

        _agent.State.HasTemperature.Should().BeTrue();
        _agent.State.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenMaxTokensIsExplicitZero_ShouldPreserveState()
    {
        var command = CreateInitializeCommand();
        command.MaxTokens = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillDefinitionInitializedEvent>();
        initialized.HasMaxTokens.Should().BeTrue();
        initialized.MaxTokens.Should().Be(0);

        _agent.State.HasMaxTokens.Should().BeTrue();
        _agent.State.MaxTokens.Should().Be(0);
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldAwaitUpsertDispatchBeforeFiringExecutionUpdate()
    {
        // Issue #440 regression: pre-PR #451 the SkillRunner reached the catalog via
        // OrleansActor.HandleEventAsync, which produced to a stream (fire-and-forget).
        // Two dispatches from the same SkillRunner turn (post-init Upsert + post-trigger
        // ExecutionUpdate) could arrive at the catalog grain out of order; the
        // ExecutionUpdate would land first, hit the missing-entry guard, and be silently
        // dropped — leaving /agent-status reporting Last run / Next run as n/a.
        //
        // PR #451 wired dispatch through IActorDispatchPort.DispatchAsync, which awaits
        // grain.HandleEnvelopeAsync. The contract that protects the catalog is: the
        // SkillRunner must AWAIT each dispatch before firing the next so the catalog
        // observes Upsert before ExecutionUpdate. To guard the contract (not the
        // synchronous shortcut a fake might enable), this test hangs the Upsert dispatch
        // on a TaskCompletionSource and asserts ExecutionUpdate is not even dispatched
        // until the gate releases. A regression that drops the await — or anything that
        // returns control before the catalog observes Upsert — would let ExecutionUpdate
        // race ahead while Upsert is still hanging, and the assertion catches it.

        var upsertGate = new TaskCompletionSource();
        var upsertDispatchStarted = new TaskCompletionSource();
        var executionDispatchStarted = new TaskCompletionSource();

        var scheduler = Substitute.For<Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>();
        scheduler
            .ScheduleTimeoutAsync(
                Arg.Any<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>();
                return Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                    req.ActorId, req.CallbackId, 1L,
                    Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
            });
        scheduler.CancelAsync(
                Arg.Any<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var catalogProxy = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogProxy));

        UserAgentCatalogGAgent? catalog = null;
        var dispatch = Substitute.For<IActorDispatchPort>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(call => DispatchGated(
                call.Arg<EventEnvelope>(),
                call.Arg<CancellationToken>(),
                catalog!,
                upsertGate,
                upsertDispatchStarted,
                executionDispatchStarted));

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
                services.AddSingleton(scheduler);
            });

        catalog = new UserAgentCatalogGAgent
        {
            Services = provider,
            EventSourcingBehaviorFactory =
                provider.GetRequiredService<IEventSourcingBehaviorFactory<UserAgentCatalogState>>(),
        };
        AssignActorId(catalog, UserAgentCatalogGAgent.WellKnownId);
        await catalog.ActivateAsync();

        var agent = CreateAgent("skill-runner-440-regression", provider);
        await agent.ActivateAsync();

        var init = CreateInitializeCommand();
        init.ScheduleCron = "0 9 * * *";
        init.ScheduleTimezone = "UTC";

        // Kick off init in the background. Upsert will hit the gate and yield; control
        // returns to the test before ExecutionUpdate has a chance to dispatch.
        var initTask = agent.HandleInitializeAsync(init);
        await upsertDispatchStarted.Task;

        // Critical assertion: while Upsert is hanging at the gate, ExecutionUpdate must
        // not have been dispatched. If the SkillRunner regressed to fire-and-forget
        // (`_ = DispatchAsync(...)` instead of `await DispatchAsync(...)`),
        // executionDispatchStarted would already be completed here.
        executionDispatchStarted.Task.IsCompleted.Should().BeFalse(
            "the SkillRunner must await Upsert's dispatch task before firing ExecutionUpdate; "
            + "regressing to fire-and-forget would let ExecutionUpdate race ahead of Upsert "
            + "and be dropped by the missing-entry guard in HandleExecutionUpdateAsync");

        // Release Upsert; ExecutionUpdate must now fire and the catalog must observe both.
        upsertGate.SetResult();
        await initTask;

        executionDispatchStarted.Task.IsCompleted.Should().BeTrue(
            "ExecutionUpdate must dispatch after Upsert completes so /agent-status shows Next run");
        catalog.State.Entries.Should().ContainSingle();
        var entry = catalog.State.Entries[0];
        entry.AgentId.Should().Be("skill-runner-440-regression");
        entry.Status.Should().Be(SkillDefinitionDefaults.StatusRunning);
        entry.ScheduleCron.Should().Be("0 9 * * *");
        entry.NextRunAt.Should().NotBeNull(
            "init's post-Upsert ExecutionUpdate must land at the catalog so /agent-status shows Next run");
    }

    private static async Task DispatchGated(
        EventEnvelope envelope,
        CancellationToken ct,
        UserAgentCatalogGAgent catalog,
        TaskCompletionSource upsertGate,
        TaskCompletionSource upsertDispatchStarted,
        TaskCompletionSource executionDispatchStarted)
    {
        if (envelope.Payload.Is(UserAgentCatalogUpsertCommand.Descriptor))
        {
            upsertDispatchStarted.TrySetResult();
            await upsertGate.Task;
        }
        else if (envelope.Payload.Is(UserAgentCatalogExecutionUpdateCommand.Descriptor))
        {
            executionDispatchStarted.TrySetResult();
        }

        await catalog.HandleEventAsync(envelope, ct);
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldDispatchCatalogCommandsThroughDispatchPort()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-dispatch-test", provider);
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(CreateInitializeCommand());

        captured.Should().HaveCount(2);
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        captured[1].Payload.Is(UserAgentCatalogExecutionUpdateCommand.Descriptor).Should().BeTrue();
        captured.Should().OnlyContain(envelope =>
            envelope.Route.PublisherActorId == "skill-runner-dispatch-test" &&
            envelope.Route.Direct.TargetActorId == UserAgentCatalogGAgent.WellKnownId);
        await catalogActor.DidNotReceive()
            .HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleExecutionCompletedAsync_WhenDefinitionDisabled_ShouldKeepCatalogStatusDisabled()
    {
        var captured = new List<EventEnvelope>();
        using var provider = BuildServiceProviderWithCatalogDispatch(new InMemoryEventStore(), captured);
        var agent = CreateAgent("skill-definition-disabled-complete", provider);
        await agent.ActivateAsync();

        var completedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await agent.HandleExecutionCompletedAsync(new ReportSkillExecutionCompletedCommand
        {
            ExecutionId = "exec-1",
            CompletedAt = completedAt,
        });

        var update = captured.Should().ContainSingle().Subject.Payload.Unpack<UserAgentCatalogExecutionUpdateCommand>();
        update.AgentId.Should().Be("skill-definition-disabled-complete");
        update.Status.Should().Be(SkillDefinitionDefaults.StatusDisabled);
        update.LastRunAt.Should().Be(completedAt);
        update.NextRunAt.Should().BeNull();
        update.ErrorCount.Should().Be(0);
        update.LastError.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleExecutionFailedAsync_WhenDefinitionDisabled_ShouldKeepStatusDisabledAndRecordFailure()
    {
        var captured = new List<EventEnvelope>();
        using var provider = BuildServiceProviderWithCatalogDispatch(new InMemoryEventStore(), captured);
        var agent = CreateAgent("skill-definition-disabled-failure", provider);
        await agent.ActivateAsync();

        var failedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await agent.HandleExecutionFailedAsync(new ReportSkillExecutionFailedCommand
        {
            ExecutionId = "exec-2",
            FailedAt = failedAt,
            Error = "delivery rejected",
            RetryAttempt = 1,
        });

        var update = captured.Should().ContainSingle().Subject.Payload.Unpack<UserAgentCatalogExecutionUpdateCommand>();
        update.AgentId.Should().Be("skill-definition-disabled-failure");
        update.Status.Should().Be(SkillDefinitionDefaults.StatusDisabled);
        update.LastRunAt.Should().Be(failedAt);
        update.NextRunAt.Should().BeNull();
        update.ErrorCount.Should().Be(2);
        update.LastError.Should().Be("delivery rejected");
    }

    [Fact]
    public async Task HandleExecutionCompletedAsync_WhenReportRevisionIsStale_ShouldIgnoreCatalogUpdate()
    {
        var captured = new List<EventEnvelope>();
        var store = new InMemoryEventStore();
        using var provider = BuildServiceProviderWithCatalogDispatch(store, captured);
        var agent = CreateAgent("skill-definition-stale-completion", provider);
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(CreateInitializeCommand());
        var runStartRevision = agent.State.ConfigRevision;

        await agent.HandleDisableAsync(new DisableSkillDefinitionCommand { Reason = "pause" });
        await agent.HandleEnableAsync(new EnableSkillDefinitionCommand { Reason = "resume" });
        agent.State.ConfigRevision.Should().BeGreaterThan(runStartRevision);

        captured.Clear();
        var completedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await agent.HandleExecutionCompletedAsync(new ReportSkillExecutionCompletedCommand
        {
            ExecutionId = "exec-stale",
            CompletedAt = completedAt,
            DefinitionConfigRevision = runStartRevision,
        });

        captured.Should().BeEmpty("a stale execution report must not overwrite the re-enabled definition's catalog row");
        var persisted = await store.GetEventsAsync("skill-definition-stale-completion");
        persisted.Should().NotContain(x => x.EventData.Is(SkillDefinitionExecutionCompletedEvent.Descriptor));

        await agent.HandleExecutionCompletedAsync(new ReportSkillExecutionCompletedCommand
        {
            ExecutionId = "exec-current",
            CompletedAt = completedAt,
            DefinitionConfigRevision = agent.State.ConfigRevision,
        });

        var update = captured.Should().ContainSingle().Subject.Payload.Unpack<UserAgentCatalogExecutionUpdateCommand>();
        update.AgentId.Should().Be("skill-definition-stale-completion");
        update.LastRunAt.Should().Be(completedAt);
    }

    private SkillDefinitionGAgent CreateAgent(string actorId, ServiceProvider? serviceProvider = null)
    {
        var resolvedServices = serviceProvider ?? _serviceProvider;
        var agent = new SkillDefinitionGAgent
        {
            Services = resolvedServices,
            EventSourcingBehaviorFactory =
                resolvedServices.GetRequiredService<IEventSourcingBehaviorFactory<SkillDefinitionState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildServiceProvider(
        IEventStore eventStore,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildServiceProviderWithCatalogDispatch(
        IEventStore eventStore,
        List<EventEnvelope> captured)
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return BuildServiceProvider(
            eventStore,
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
    }

    private static InitializeSkillDefinitionCommand CreateInitializeCommand() => new()
    {
        SkillName = "daily_report",
        TemplateName = "daily_report",
        SkillContent = "You are a daily report runner.",
        ExecutionPrompt = "Run the report.",
        ScheduleCron = string.Empty,
        ScheduleTimezone = SkillDefinitionDefaults.DefaultTimezone,
        Enabled = true,
        ScopeId = "scope-1",
        ProviderName = SkillDefinitionDefaults.DefaultProviderName,
        OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
        },
    };

    private static void AssignActorId(GAgentBase agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
