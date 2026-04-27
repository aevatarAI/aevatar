using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationGAgentTests : IAsyncLifetime
{
    private ChannelBotRegistrationGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));

        _serviceProvider = services.BuildServiceProvider();

        _agent = new ChannelBotRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleRegister_PersistsLarkRelayRegistration()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1",
            RequestedId = "reg-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
            NyxConversationRouteId = "route-1",
        });

        _agent.State.Registrations.Should().ContainSingle();
        var entry = _agent.State.Registrations[0];
        entry.Id.Should().Be("reg-1");
        entry.Platform.Should().Be("lark");
        entry.NyxProviderSlug.Should().Be("api-lark-bot");
        entry.ScopeId.Should().Be("scope-1");
        entry.WebhookUrl.Should().Contain("/api/v1/webhooks/channel/lark/");
        entry.NyxChannelBotId.Should().Be("bot-1");
        entry.NyxAgentApiKeyId.Should().Be("key-1");
        entry.NyxConversationRouteId.Should().Be("route-1");
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRegister_PersistsTelegramRelayRegistration()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "telegram",
            NyxProviderSlug = "api-telegram-bot",
            ScopeId = "scope-1",
            WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1",
            RequestedId = "reg-telegram",
            NyxChannelBotId = "bot-tg-1",
            NyxAgentApiKeyId = "key-tg-1",
            NyxConversationRouteId = "route-tg-1",
        });

        _agent.State.Registrations.Should().ContainSingle();
        var entry = _agent.State.Registrations[0];
        entry.Id.Should().Be("reg-telegram");
        entry.Platform.Should().Be("telegram");
        entry.NyxProviderSlug.Should().Be("api-telegram-bot");
        entry.ScopeId.Should().Be("scope-1");
        entry.WebhookUrl.Should().Contain("/api/v1/webhooks/channel/telegram/");
        entry.NyxChannelBotId.Should().Be("bot-tg-1");
        entry.NyxAgentApiKeyId.Should().Be("key-tg-1");
        entry.NyxConversationRouteId.Should().Be("route-tg-1");
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRegister_IgnoresUnsupportedPlatforms()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "discord",
            NyxProviderSlug = "api-discord-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-discord",
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRegister_RejectsLarkRegistrationWithoutScopeId()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUnregister_TombstonesEntry()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
        });

        await _agent.HandleUnregister(new ChannelBotUnregisterCommand
        {
            RegistrationId = "reg-1",
        });

        _agent.State.Registrations.Should().ContainSingle();
        _agent.State.Registrations[0].Tombstoned.Should().BeTrue();
        _agent.State.Registrations[0].TombstoneStateVersion.Should().BePositive();
    }

    [Fact]
    public async Task HandleCompactTombstones_RemovesWatermarkPassedEntries()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
        });

        await _agent.HandleUnregister(new ChannelBotUnregisterCommand
        {
            RegistrationId = "reg-1",
        });

        var safeStateVersion = _agent.State.Registrations[0].TombstoneStateVersion;
        await _agent.HandleCompactTombstones(new ChannelBotCompactTombstonesCommand
        {
            SafeStateVersion = safeStateVersion,
        });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRebuildProjection_PersistsRefreshEvent_WithoutMutatingState()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

        var beforeState = _agent.State.Clone();
        var beforeVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRebuildProjection(new ChannelBotRebuildProjectionCommand
        {
            Reason = "test-rebuild",
        });

        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeVersion + 1);
        _agent.State.Should().BeEquivalentTo(beforeState);
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
            var latest = stream.Count == 0 ? 0 : stream[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
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
