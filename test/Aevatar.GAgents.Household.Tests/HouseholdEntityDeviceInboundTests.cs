using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.Household.Tests;

/// <summary>
/// Tests for <see cref="HouseholdEntity.HandleDeviceInbound"/> — validates that
/// inbound device events are correctly dispatched, parsed, and applied to state.
/// </summary>
public class HouseholdEntityDeviceInboundTests : IAsyncLifetime
{
    private HouseholdEntity _entity = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStoreForHouseholdTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));

        _serviceProvider = services.BuildServiceProvider();

        _entity = new HouseholdEntity(new StubLLMProviderFactory())
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<HouseholdEntityState>>(),
        };

        await _entity.ActivateAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandleDeviceInbound_TemperatureChange_UpdatesEnvironment()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-1",
            Source = "temperature-sensor",
            EventType = "temperature_change",
            PayloadJson = """{"temperature": 28.5, "humidity": 65.0, "light_level": 70.0}""",
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.Temperature.Should().Be(28.5);
        _entity.State.Environment.Humidity.Should().Be(65.0);
        _entity.State.Environment.LightLevel.Should().Be(70.0);
    }

    [Fact]
    public async Task HandleDeviceInbound_PersonDetected_UpdatesSceneDescription()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-2",
            Source = "camera-analyzer",
            EventType = "person_detected",
            PayloadJson = """{"description": "Two people sitting in the living room"}""",
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.SceneDescription.Should().Be("Two people sitting in the living room");
    }

    [Fact]
    public async Task HandleDeviceInbound_MotionDetected_UpdatesMotionFlag()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-3",
            Source = "motion-sensor",
            EventType = "motion_detected",
            PayloadJson = "{}",
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.MotionDetected.Should().BeTrue();
    }

    [Fact]
    public async Task HandleDeviceInbound_UnknownEventType_NoStateChange()
    {
        // Capture baseline state after activation
        var prevTemp = _entity.State.Environment?.Temperature ?? 0;
        var prevScene = _entity.State.Environment?.SceneDescription ?? "";
        var prevMotion = _entity.State.Environment?.MotionDetected ?? false;

        var evt = new DeviceInbound
        {
            EventId = "evt-4",
            Source = "unknown-device",
            EventType = "unknown_type",
            PayloadJson = """{"foo": "bar"}""",
        };

        // Should not throw and should not change state
        var act = () => _entity.HandleDeviceInbound(evt);
        await act.Should().NotThrowAsync();

        _entity.State.Environment!.Temperature.Should().Be(prevTemp);
        _entity.State.Environment!.SceneDescription.Should().Be(prevScene);
        _entity.State.Environment!.MotionDetected.Should().Be(prevMotion);
    }

    [Fact]
    public async Task HandleDeviceInbound_MalformedPayloadJson_NoStateChange()
    {
        var prevTemp = _entity.State.Environment?.Temperature ?? 0;

        var evt = new DeviceInbound
        {
            EventId = "evt-5",
            Source = "temperature-sensor",
            EventType = "temperature_change",
            PayloadJson = "not valid json",
        };

        // Should not throw — the handler catches JsonException
        var act = () => _entity.HandleDeviceInbound(evt);
        await act.Should().NotThrowAsync();

        _entity.State.Environment!.Temperature.Should().Be(prevTemp);
    }

    [Fact]
    public async Task HandleDeviceInbound_SpeechDetected_DoesNotThrow()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-6",
            Source = "microphone",
            EventType = "speech_detected",
            PayloadJson = """{"text": "Turn on the lights"}""",
        };

        // speech_detected triggers HandleChat -> RunReasoningAsync -> ChatAsync.
        // With the stub LLM provider, ChatAsync returns "ok" (no tool calls).
        // The handler should complete without throwing.
        var act = () => _entity.HandleDeviceInbound(evt);
        await act.Should().NotThrowAsync();
    }

    // ─── Test doubles ───

    private sealed class InMemoryEventStoreForHouseholdTests : IEventStore
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

    private sealed class StubLLMProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => new StubLLMProvider(name);
        public ILLMProvider GetDefault() => new StubLLMProvider("default");
        public IReadOnlyList<string> GetAvailableProviders() => ["default"];
    }

    private sealed class StubLLMProvider(string name) : ILLMProvider
    {
        public string Name => name;

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse { Content = "NO_ACTION — no intervention needed." });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
