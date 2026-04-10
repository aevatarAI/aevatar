using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Unit tests for <see cref="ChannelBotRegistrationGAgent"/> — validates command handling,
/// state transitions, and event sourcing behavior.
/// </summary>
public class ChannelBotRegistrationGAgentTests : IAsyncLifetime
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

    public async Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandleRegister_CreatesEntryInState()
    {
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = "token-123",
            VerificationToken = "verify-456",
            ScopeId = "scope-1",
        };

        await _agent.HandleRegister(cmd);

        _agent.State.Registrations.Should().HaveCount(1);
        var entry = _agent.State.Registrations[0];
        entry.Platform.Should().Be("lark");
        entry.NyxProviderSlug.Should().Be("api-lark-bot");
        entry.NyxUserToken.Should().Be("token-123");
        entry.VerificationToken.Should().Be("verify-456");
        entry.ScopeId.Should().Be("scope-1");
        entry.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleRegister_GeneratesUniqueId()
    {
        var cmd1 = new ChannelBotRegisterCommand { Platform = "lark", NyxProviderSlug = "slug-1", NyxUserToken = "t1" };
        var cmd2 = new ChannelBotRegisterCommand { Platform = "telegram", NyxProviderSlug = "slug-2", NyxUserToken = "t2" };

        await _agent.HandleRegister(cmd1);
        await _agent.HandleRegister(cmd2);

        _agent.State.Registrations.Should().HaveCount(2);
        var id1 = _agent.State.Registrations[0].Id;
        var id2 = _agent.State.Registrations[1].Id;
        id1.Should().NotBe(id2);
    }

    [Fact]
    public async Task HandleRegister_AllFieldsPersisted()
    {
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = "token-abc",
            VerificationToken = "verify-xyz",
            ScopeId = "scope-x",
            WebhookUrl = "https://example.com/callback",
        };

        await _agent.HandleRegister(cmd);

        _agent.State.Registrations.Should().HaveCount(1);
        var entry = _agent.State.Registrations[0];
        entry.Platform.Should().Be("lark");
        entry.NyxProviderSlug.Should().Be("api-lark-bot");
        entry.NyxUserToken.Should().Be("token-abc");
        entry.VerificationToken.Should().Be("verify-xyz");
        entry.ScopeId.Should().Be("scope-x");
        entry.WebhookUrl.Should().Be("https://example.com/callback");
        entry.CreatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRegister_NullOptionalFieldsStoreEmpty()
    {
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "slug",
            NyxUserToken = "token",
        };

        await _agent.HandleRegister(cmd);

        var entry = _agent.State.Registrations[0];
        entry.VerificationToken.Should().BeEmpty();
        entry.ScopeId.Should().BeEmpty();
        entry.WebhookUrl.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUnregister_RemovesEntry()
    {
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "slug",
            NyxUserToken = "token",
        };
        await _agent.HandleRegister(cmd);

        var registrationId = _agent.State.Registrations[0].Id;

        await _agent.HandleUnregister(new ChannelBotUnregisterCommand { RegistrationId = registrationId });

        _agent.State.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUnregister_NonExistent_NoStateChange()
    {
        var cmd = new ChannelBotRegisterCommand
        {
            Platform = "lark",
            NyxProviderSlug = "slug",
            NyxUserToken = "token",
        };
        await _agent.HandleRegister(cmd);

        // Unregister a non-existent ID — should not throw and should not change state
        var act = () => _agent.HandleUnregister(
            new ChannelBotUnregisterCommand { RegistrationId = "does-not-exist" });

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
