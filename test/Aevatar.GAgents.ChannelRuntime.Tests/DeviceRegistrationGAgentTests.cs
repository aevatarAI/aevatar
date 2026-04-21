using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceRegistrationGAgent"/> — validates command handling,
/// state transitions, and event sourcing behavior.
/// </summary>
public class DeviceRegistrationGAgentTests : IAsyncLifetime
{
    private DeviceRegistrationGAgent _agent = null!;
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

        _agent = new DeviceRegistrationGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<DeviceRegistrationState>>(),
        };

        await _agent.ActivateAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandleRegister_CreatesEntryInState()
    {
        var cmd = new DeviceRegisterCommand
        {
            ScopeId = "scope-1",
            HmacKey = "key-1",
            Description = "Test device",
        };

        await _agent.HandleRegister(cmd);

        _agent.State.Registrations.Should().HaveCount(1);
        var entry = _agent.State.Registrations[0];
        entry.ScopeId.Should().Be("scope-1");
        entry.HmacKey.Should().Be("key-1");
        entry.Description.Should().Be("Test device");
        entry.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleRegister_GeneratesUniqueId()
    {
        var cmd1 = new DeviceRegisterCommand { ScopeId = "scope-a", HmacKey = "k1" };
        var cmd2 = new DeviceRegisterCommand { ScopeId = "scope-b", HmacKey = "k2" };

        await _agent.HandleRegister(cmd1);
        await _agent.HandleRegister(cmd2);

        _agent.State.Registrations.Should().HaveCount(2);
        var id1 = _agent.State.Registrations[0].Id;
        var id2 = _agent.State.Registrations[1].Id;
        id1.Should().NotBe(id2);
    }

    [Fact]
    public async Task HandleRegister_RequiredFieldsPersisted()
    {
        var cmd = new DeviceRegisterCommand
        {
            ScopeId = "scope-x",
            HmacKey = "hmac-secret",
            NyxConversationId = "conv-42",
            Description = "Living room sensor hub",
        };

        await _agent.HandleRegister(cmd);

        _agent.State.Registrations.Should().HaveCount(1);
        var entry = _agent.State.Registrations[0];
        entry.ScopeId.Should().Be("scope-x");
        entry.HmacKey.Should().Be("hmac-secret");
        entry.NyxConversationId.Should().Be("conv-42");
        entry.Description.Should().Be("Living room sensor hub");
        entry.CreatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleUnregister_TombstonesEntry()
    {
        var cmd = new DeviceRegisterCommand { ScopeId = "scope-del", HmacKey = "k" };
        await _agent.HandleRegister(cmd);

        var registrationId = _agent.State.Registrations[0].Id;

        await _agent.HandleUnregister(new DeviceUnregisterCommand { RegistrationId = registrationId });

        // Entry is retained as a tombstone so the projector can emit a Tombstone verdict
        // (Channel RFC §7.1.1). A separate housekeeping job cleans watermark-passed tombstones.
        _agent.State.Registrations.Should().ContainSingle();
        _agent.State.Registrations[0].Id.Should().Be(registrationId);
        _agent.State.Registrations[0].Tombstoned.Should().BeTrue();
        _agent.State.Registrations[0].TombstoneStateVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleCompactTombstones_RemovesOnlyWatermarkSafeEntries()
    {
        await _agent.HandleRegister(new DeviceRegisterCommand { ScopeId = "scope-a", HmacKey = "key-a" });
        var tombstonedId = _agent.State.Registrations[0].Id;
        await _agent.HandleUnregister(new DeviceUnregisterCommand { RegistrationId = tombstonedId });

        await _agent.HandleRegister(new DeviceRegisterCommand { ScopeId = "scope-b", HmacKey = "key-b" });
        var liveId = _agent.State.Registrations[1].Id;

        await _agent.HandleCompactTombstones(new DeviceCompactTombstonesCommand { SafeStateVersion = 1 });
        _agent.State.Registrations.Select(x => x.Id).Should().Contain(tombstonedId);
        _agent.State.Registrations.Select(x => x.Id).Should().Contain(liveId);

        await _agent.HandleCompactTombstones(new DeviceCompactTombstonesCommand { SafeStateVersion = 2 });

        _agent.State.Registrations.Should().ContainSingle();
        _agent.State.Registrations[0].Id.Should().Be(liveId);
        _agent.State.Registrations[0].Tombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task HandleUnregister_NonExistent_NoStateChange()
    {
        var cmd = new DeviceRegisterCommand { ScopeId = "scope-keep", HmacKey = "k" };
        await _agent.HandleRegister(cmd);

        // Unregister a non-existent ID — should not throw and should not change state
        var act = () => _agent.HandleUnregister(
            new DeviceUnregisterCommand { RegistrationId = "does-not-exist" });

        await act.Should().NotThrowAsync();
        _agent.State.Registrations.Should().HaveCount(1);
    }

    // ─── Test double ───

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
