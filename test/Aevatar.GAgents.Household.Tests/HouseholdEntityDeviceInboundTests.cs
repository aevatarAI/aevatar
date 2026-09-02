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
/// typed inbound device events are correctly dispatched and applied to state.
/// </summary>
public class HouseholdEntityDeviceInboundTests : IAsyncLifetime
{
    private HouseholdEntity _entity = null!;
    private RecordingLLMProvider _llmProvider = null!;
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

        _llmProvider = new RecordingLLMProvider("default");
        _entity = new HouseholdEntity(
            UnexpectedAgentToolExecutionPort.Instance,
            new StubLLMProviderFactory(_llmProvider))
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
    public async Task HandleDeviceInbound_TypedTemperature_UpdatesEnvironment()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-1",
            Source = "temperature-sensor",
            EventType = "temperature_change",
            Sensor = new SensorDeviceInboundPayload
            {
                Temperature = 28.5,
                Humidity = 65.0,
                LightLevel = 70.0,
            },
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.Temperature.Should().Be(28.5);
        _entity.State.Environment.Humidity.Should().Be(65.0);
        _entity.State.Environment.LightLevel.Should().Be(70.0);
    }

    [Fact]
    public async Task HandleDeviceInbound_TypedCamera_UpdatesSceneDescription()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-2",
            Source = "camera-analyzer",
            EventType = "person_detected",
            Camera = new CameraDeviceInboundPayload
            {
                SceneDescription = "Two people sitting in the living room",
            },
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.SceneDescription.Should().Be("Two people sitting in the living room");
    }

    [Fact]
    public async Task HandleDeviceInbound_TypedMotion_UpdatesMotionFlag()
    {
        var evt = new DeviceInbound
        {
            EventId = "evt-3",
            Source = "motion-sensor",
            EventType = "motion_detected",
            Motion = new MotionDeviceInboundPayload { Detected = true },
        };

        await _entity.HandleDeviceInbound(evt);

        _entity.State.Environment.Should().NotBeNull();
        _entity.State.Environment.MotionDetected.Should().BeTrue();
    }

    [Fact]
    public async Task HandleDeviceInbound_NoTypedPayload_NoStateChange()
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
        };

        // Should not throw and should not change state
        var act = () => _entity.HandleDeviceInbound(evt);
        await act.Should().NotThrowAsync();

        _entity.State.Environment!.Temperature.Should().Be(prevTemp);
        _entity.State.Environment!.SceneDescription.Should().Be(prevScene);
        _entity.State.Environment!.MotionDetected.Should().Be(prevMotion);
    }

    [Fact]
    public async Task HandleDeviceInbound_TypedSpeech_ForwardsTextToReasoning()
    {
        var previousReasoningCount = _entity.State.ReasoningCountToday;
        var previousLastReasoningTs = _entity.State.LastReasoningTs;

        var evt = new DeviceInbound
        {
            EventId = "evt-6",
            Source = "microphone",
            EventType = "speech_detected",
            Speech = new SpeechDeviceInboundPayload { Text = "Turn on the lights" },
        };

        await _entity.HandleDeviceInbound(evt);

        _llmProvider.CallCount.Should().Be(1);
        _llmProvider.LastRequest.Should().NotBeNull();
        _llmProvider.LastRequest!.Messages.Should().Contain(message =>
            string.Equals(message.Role, "user", StringComparison.Ordinal) &&
            message.Content != null &&
            message.Content.Contains("Message from user: Turn on the lights", StringComparison.Ordinal));
        _entity.State.ReasoningCountToday.Should().Be(previousReasoningCount + 1);
        _entity.State.LastReasoningTs.Should().BeGreaterThan(previousLastReasoningTs);
    }

    [Fact]
    public async Task HandleDeviceInbound_TypedSpeech_WhenTextBlank_SkipsReasoning()
    {
        var previousReasoningCount = _entity.State.ReasoningCountToday;
        var previousLastReasoningTs = _entity.State.LastReasoningTs;

        var evt = new DeviceInbound
        {
            EventId = "evt-7",
            Source = "microphone",
            EventType = "speech_detected",
            Speech = new SpeechDeviceInboundPayload { Text = "   " },
        };

        await _entity.HandleDeviceInbound(evt);

        _llmProvider.CallCount.Should().Be(0);
        _llmProvider.LastRequest.Should().BeNull();
        _entity.State.ReasoningCountToday.Should().Be(previousReasoningCount);
        _entity.State.LastReasoningTs.Should().Be(previousLastReasoningTs);
    }

    [Fact]
    public void HouseholdEntity_DeviceInboundHandler_ShouldConsumeTypedPayloadsOnly()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../agents/Aevatar.GAgents.Household/HouseholdEntity.cs"));
        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("JsonDocument.Parse");
        source.Should().NotContain("evt.PayloadJson");
        source.Should().Contain("switch (evt.PayloadCase)");
    }

    // ─── Test doubles ───

    private sealed class InMemoryEventStoreForHouseholdTests : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public IReadOnlyList<StateEvent> SnapshotEvents()
        {
            return _events.Values
                .SelectMany(x => x)
                .OrderBy(x => x.Version)
                .Select(x => x.Clone())
                .ToList();
        }

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

    private sealed class StubLLMProviderFactory(RecordingLLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => ["default"];
    }

    private sealed class RecordingLLMProvider(string name) : ILLMProvider
    {
        public string Name => name;
        public int CallCount { get; private set; }
        public LLMRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            await Task.CompletedTask;
            yield return new LLMStreamChunk { DeltaContent = "NO_ACTION — no intervention needed." };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }
}

internal sealed class UnexpectedAgentToolExecutionPort : IAgentToolExecutionPort
{
    public static UnexpectedAgentToolExecutionPort Instance { get; } = new();

    public Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Tool '{request.Tool.Name}' must not execute in household state tests.");
}
