using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private ServiceProvider _serviceProvider = null!;
    private SkillRunnerGAgent _agent = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryEventStore();

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(_store);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));

        _serviceProvider = services.BuildServiceProvider();
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
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeFalse();
        initialized.HasMaxTokens.Should().BeFalse();

        _agent.State.HasTemperature.Should().BeFalse();
        _agent.State.HasMaxTokens.Should().BeFalse();
        _agent.State.MaxToolRounds.Should().Be(SkillRunnerDefaults.DefaultMaxToolRounds);
        _agent.State.MaxHistoryMessages.Should().Be(SkillRunnerDefaults.DefaultMaxHistoryMessages);
        _agent.EffectiveConfig.Temperature.Should().BeNull();
        _agent.EffectiveConfig.MaxTokens.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenTemperatureIsExplicitZero_ShouldPreserveIt()
    {
        var command = CreateInitializeCommand();
        command.Temperature = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeTrue();
        initialized.Temperature.Should().Be(0);

        _agent.State.HasTemperature.Should().BeTrue();
        _agent.State.Temperature.Should().Be(0);
        _agent.EffectiveConfig.Temperature.Should().Be(0);
    }

    private SkillRunnerGAgent CreateAgent(string actorId)
    {
        var agent = new SkillRunnerGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<SkillRunnerState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static InitializeSkillRunnerCommand CreateInitializeCommand() => new()
    {
        SkillName = "daily_report",
        TemplateName = "daily_report",
        SkillContent = "You are a daily report runner.",
        ExecutionPrompt = "Run the report.",
        ScheduleCron = string.Empty,
        ScheduleTimezone = SkillRunnerDefaults.DefaultTimezone,
        Enabled = true,
        ScopeId = "scope-1",
        ProviderName = SkillRunnerDefaults.DefaultProviderName,
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
