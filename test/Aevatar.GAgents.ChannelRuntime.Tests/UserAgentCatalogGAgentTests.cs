using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogGAgentTests : IAsyncLifetime
{
    private UserAgentCatalogGAgent _agent = null!;
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

        _agent = new UserAgentCatalogGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<UserAgentCatalogState>>(),
        };

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleTombstoneAsync_RecordsTombstoneStateVersion()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-a",
            Platform = "lark",
            ConversationId = "conv-a",
        });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-a",
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].AgentId.Should().Be("agent-a");
        _agent.State.Entries[0].Tombstoned.Should().BeTrue();
        _agent.State.Entries[0].TombstoneStateVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleCompactTombstonesAsync_RemovesOnlyWatermarkSafeEntries()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-a",
            Platform = "lark",
            ConversationId = "conv-a",
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-a",
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-b",
            Platform = "telegram",
            ConversationId = "conv-b",
        });

        await _agent.HandleCompactTombstonesAsync(new UserAgentCatalogCompactTombstonesCommand
        {
            SafeStateVersion = 1,
        });
        _agent.State.Entries.Select(x => x.AgentId).Should().Contain("agent-a");
        _agent.State.Entries.Select(x => x.AgentId).Should().Contain("agent-b");

        await _agent.HandleCompactTombstonesAsync(new UserAgentCatalogCompactTombstonesCommand
        {
            SafeStateVersion = 2,
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].AgentId.Should().Be("agent-b");
        _agent.State.Entries[0].Tombstoned.Should().BeFalse();
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
            {
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");
            }

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
