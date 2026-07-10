using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
        services.AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>();

        _serviceProvider = services.BuildServiceProvider();

        _agent = new ChannelBotRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(_agent, ChannelBotRegistrationGAgent.WellKnownId);

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    private ChannelBotRegistrationGAgent CreateAgent()
    {
        var agent = new ChannelBotRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(agent, ChannelBotRegistrationGAgent.WellKnownId);
        return agent;
    }

    private static void SetId(GAgentBase agent, string actorId)
    {
        var method = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("tests replay the well-known registration-store event stream");
        method!.Invoke(agent, [actorId]);
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
            NyxReplyCredentialRef = "secrets://channel/nyxid/lark/reg-1/reply-api-key",
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
        entry.NyxReplyCredentialRef.Should().Be("secrets://channel/nyxid/lark/reg-1/reply-api-key");
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRecordInbound_SetsActivationOnce_AndIsIdempotent()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            RequestedId = "reg-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
        });

        var first = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero));
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand { RegistrationId = "reg-1", ObservedAtUtc = first });
        _agent.State.Registrations.Single(r => r.Id == "reg-1").LastInboundAtUtc.Should().Be(first);

        // Activation is set once — a later inbound must NOT overwrite it (bounds the
        // single store actor's event log; we don't persist an event per message).
        var second = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 24, 11, 0, 0, TimeSpan.Zero));
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand { RegistrationId = "reg-1", ObservedAtUtc = second });
        _agent.State.Registrations.Single(r => r.Id == "reg-1").LastInboundAtUtc.Should().Be(first);
    }

    [Fact]
    public async Task HandleRecordInbound_NoopForUnknownRegistration()
    {
        await _agent.HandleRecordInbound(new ChannelBotRecordInboundCommand
        {
            RegistrationId = "does-not-exist",
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        _agent.State.Registrations.Should().BeEmpty();
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
    public async Task HandleRegister_RejectsLarkRegistrationWithoutScopeId_AndPersistsRejectionEvent()
    {
        var beforeVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

        // Audit event recorded for the contract break (issue #391); the
        // registration set stays empty because the rejection is a no-op
        // transition.
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeVersion + 1);
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
    public async Task ReplayScopeIdRepairedEvent_PreservesCreatedAt_WhenRewritingScope()
    {
        await _agent.HandleRegister(new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-original",
            RequestedId = "reg-1",
            NyxAgentApiKeyId = "key-1",
        });

        var originalCreatedAt = _agent.State.Registrations[0].CreatedAt;
        originalCreatedAt.Should().NotBeNull();
        await AppendScopeIdRepairedEventAsync("reg-1", "scope-original", "scope-repaired");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        var entry = replayed.State.Registrations.Should().ContainSingle().Subject;
        entry.ScopeId.Should().Be("scope-repaired");
        entry.CreatedAt.Should().Be(originalCreatedAt);
        entry.Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayScopeIdRepairedEvent_IgnoresTombstonedRegistration()
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
        await AppendScopeIdRepairedEventAsync("reg-1", "scope-1", "scope-2");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        replayed.State.Registrations[0].ScopeId.Should().Be("scope-1");
        replayed.State.Registrations[0].Tombstoned.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayScopeIdRepairedEvent_IgnoresMissingRegistration()
    {
        await AppendScopeIdRepairedEventAsync("reg-missing", string.Empty, "scope-1");

        var replayed = CreateAgent();
        await replayed.ActivateAsync();

        replayed.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public void ScopeIdRepairedEvent_DoesNotExposeLiveRepairCommandHandler()
    {
        typeof(ChannelBotRegistrationGAgent)
            .GetMethods()
            .Select(static method => method.Name)
            .Should()
            .NotContain("HandleRepairScopeId");
    }

    private async Task AppendScopeIdRepairedEventAsync(
        string registrationId,
        string previousScopeId,
        string scopeId)
    {
        var eventStore = _serviceProvider.GetRequiredService<IEventStore>();
        await eventStore.AppendAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            [
                new StateEvent
                {
                    AgentId = ChannelBotRegistrationGAgent.WellKnownId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = ChannelBotScopeIdRepairedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ChannelBotScopeIdRepairedEvent
                    {
                        RegistrationId = registrationId,
                        PreviousScopeId = previousScopeId,
                        ScopeId = scopeId,
                        RepairedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = _agent.EventSourcing!.CurrentVersion + 1,
                },
            ],
            _agent.EventSourcing!.CurrentVersion,
            CancellationToken.None);
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
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
