using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.Household.Tests;

public sealed class HouseholdEntityDeadlineTests
{
    [Fact]
    public async Task HostDeadline_ShouldCommitTypedTimeoutAndAllowNextChatTurn()
    {
        const int timeoutMs = 1_000;
        var store = new RecordingEventStore();
        await using var services = BuildServices(store);
        var timeProvider = new FakeTimeProvider();
        var provider = new HangingThenSuccessfulProvider();
        var agent = CreateAgent(services, provider, timeProvider, timeoutMs);
        await agent.ActivateAsync();

        var timedOutTurn = agent.HandleChat(new HouseholdChatEvent { Prompt = "wait forever" });
        await provider.FirstStreamStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await timedOutTurn;

        agent.State.LastReasoningTerminal.Should().NotBeNull();
        agent.State.LastReasoningTerminal.Outcome.Should().Be(HouseholdReasoningOutcome.Failed);
        agent.State.LastReasoningTerminal.FailureCode.Should().Be("LLM_TIMEOUT");
        agent.State.LastReasoningTerminal.SafeMessage.Should().Contain("exceeded its deadline");
        agent.State.ReasoningCountToday.Should().Be(0);

        await agent.HandleChat(new HouseholdChatEvent { Prompt = "next message" });

        provider.StreamCallCount.Should().Be(2);
        agent.State.LastReasoningTerminal.Outcome.Should().Be(HouseholdReasoningOutcome.Completed);
        agent.State.LastReasoningTerminal.Reasoning.Should().Be("NO_ACTION - next turn completed");
        agent.State.ReasoningCountToday.Should().Be(1);
        Completions(store, agent.Id).Should().ContainSingle(completion =>
            completion.Outcome == HouseholdReasoningOutcome.Failed &&
            completion.FailureCode == "LLM_TIMEOUT");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostDeadline_ShouldRejectLateProviderYieldAndLateNormalEnd(bool yieldLateChunk)
    {
        const int timeoutMs = 1_000;
        var store = new RecordingEventStore();
        await using var services = BuildServices(store);
        var timeProvider = new FakeTimeProvider();
        var provider = new LateAfterCancellationProvider(yieldLateChunk);
        var agent = CreateAgent(services, provider, timeProvider, timeoutMs);
        await agent.ActivateAsync();

        var turn = agent.HandleChat(new HouseholdChatEvent { Prompt = "ignore cancellation" });
        await provider.StreamStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await provider.CancellationObserved;
        provider.ReleaseAfterCancellation();
        await turn;

        var completion = Completions(store, agent.Id).Should().ContainSingle().Which;
        completion.Outcome.Should().Be(HouseholdReasoningOutcome.Failed);
        completion.FailureCode.Should().Be("LLM_TIMEOUT");
        completion.Reasoning.Should().NotContain(LateAfterCancellationProvider.LateContent);
        agent.State.LastReasoningTerminal.Outcome.Should().Be(HouseholdReasoningOutcome.Failed);
        agent.State.LastReasoningTerminal.Reasoning.Should().NotContain(LateAfterCancellationProvider.LateContent);
    }

    [Fact]
    public async Task HostDeadline_WhenSuccessReasoningCommitWaitsPastDeadline_ShouldCommitOnlyTypedTimeout()
    {
        const int timeoutMs = 1_000;
        var store = new BlockingHouseholdSuccessEventStore();
        await using var services = BuildServices(store);
        var timeProvider = new FakeTimeProvider();
        var agent = CreateAgent(services, new SuccessfulProvider(), timeProvider, timeoutMs);
        await agent.ActivateAsync();

        var turn = agent.HandleChat(new HouseholdChatEvent { Prompt = "finish then wait on persistence" });
        await store.SuccessAppendStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await store.CancellationObserved;
        await turn;

        var completion = Completions(store.Inner, agent.Id).Should().ContainSingle().Which;
        completion.Outcome.Should().Be(HouseholdReasoningOutcome.Failed);
        completion.FailureCode.Should().Be("LLM_TIMEOUT");
        completion.Reasoning.Should().NotContain("successful household response");
        agent.State.LastReasoningTerminal.Outcome.Should().Be(HouseholdReasoningOutcome.Failed);
        agent.State.ReasoningCountToday.Should().Be(0);
    }

    [Fact]
    public async Task HostDeadline_WhenSuccessCommitResultReturnsAfterDeadline_ShouldKeepCommittedSuccess()
    {
        const int timeoutMs = 1_000;
        var store = new LateReturningCommittedHouseholdSuccessEventStore();
        await using var services = BuildServices(store);
        var timeProvider = new FakeTimeProvider();
        var agent = CreateAgent(services, new SuccessfulProvider(), timeProvider, timeoutMs);
        await agent.ActivateAsync();

        var turn = agent.HandleChat(new HouseholdChatEvent
        {
            Prompt = "commit before the deadline result returns",
        });
        await store.SuccessCommitCompleted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await store.DeadlineObserved;
        await turn;

        var completion = Completions(store.Inner, agent.Id).Should().ContainSingle().Which;
        completion.Outcome.Should().Be(HouseholdReasoningOutcome.Completed);
        completion.FailureCode.Should().BeEmpty();
        completion.Reasoning.Should().Be("NO_ACTION - successful household response");
        agent.State.LastReasoningTerminal.Outcome.Should().Be(HouseholdReasoningOutcome.Completed);
        agent.State.ReasoningCountToday.Should().Be(1);
    }

    private static HouseholdEntity CreateAgent(
        ServiceProvider services,
        ILLMProviderFactory provider,
        TimeProvider timeProvider,
        int timeoutMs) =>
        new(
            UnexpectedAgentToolExecutionPort.Instance,
            provider,
            chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs),
            timeProvider: timeProvider)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<HouseholdEntityState>>(),
        };

    private static ServiceProvider BuildServices(IEventStore store) =>
        new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IEventStore>(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(
                typeof(IEventSourcingBehaviorFactory<>),
                typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

    private static IReadOnlyList<ReasoningCompletedEvent> Completions(
        RecordingEventStore store,
        string actorId) =>
        store.EventsFor(actorId)
            .Where(stateEvent => stateEvent.EventData.Is(ReasoningCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<ReasoningCompletedEvent>())
            .ToArray();

    private sealed class HangingThenSuccessfulProvider : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _firstStreamStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCallCount;

        public string Name => "household-deadline";
        public Task FirstStreamStarted => _firstStreamStarted.Task;
        public int StreamCallCount => _streamCallCount;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            if (Interlocked.Increment(ref _streamCallCount) == 1)
            {
                _firstStreamStarted.TrySetResult();
                await _neverCompletes.Task.WaitAsync(ct);
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "NO_ACTION - next turn completed" };
        }
    }

    private sealed class LateAfterCancellationProvider(bool yieldLateChunk)
        : ILLMProviderFactory, ILLMProvider
    {
        public const string LateContent = "late household provider content";
        private readonly TaskCompletionSource _streamStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAfterCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "household-late-provider";
        public Task StreamStarted => _streamStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];
        public void ReleaseAfterCancellation() => _releaseAfterCancellation.TrySetResult();

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            _streamStarted.TrySetResult();
            try
            {
                await _neverCompletes.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
            }

            await _releaseAfterCancellation.Task;
            if (yieldLateChunk)
                yield return new LLMStreamChunk { DeltaContent = LateContent };
        }
    }

    private sealed class SuccessfulProvider : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "household-success";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "NO_ACTION - successful household response" };
            await Task.CompletedTask;
        }
    }

    private sealed class BlockingHouseholdSuccessEventStore : IEventStore
    {
        private readonly TaskCompletionSource _appendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingEventStore Inner { get; } = new();
        public Task SuccessAppendStarted => _appendStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (batch.Any(IsSuccessfulReasoningCompletion))
            {
                _appendStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }

            return await Inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            Inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            Inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            Inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        private static bool IsSuccessfulReasoningCompletion(StateEvent stateEvent) =>
            stateEvent.EventData.Is(ReasoningCompletedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<ReasoningCompletedEvent>().Outcome ==
            HouseholdReasoningOutcome.Completed;
    }

    private sealed class LateReturningCommittedHouseholdSuccessEventStore : IEventStore
    {
        private readonly TaskCompletionSource _successCommitCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deadlineObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingEventStore Inner { get; } = new();
        public Task SuccessCommitCompleted => _successCommitCompleted.Task;
        public Task DeadlineObserved => _deadlineObserved.Task;

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (!batch.Any(IsSuccessfulReasoningCompletion))
                return await Inner.AppendAsync(agentId, batch, expectedVersion, ct);

            var committed = await Inner.AppendAsync(
                agentId,
                batch,
                expectedVersion,
                CancellationToken.None);
            _successCommitCompleted.TrySetResult();
            using var registration = ct.Register(() => _deadlineObserved.TrySetResult());
            await _deadlineObserved.Task;
            return committed;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            Inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            Inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            Inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

        private static bool IsSuccessfulReasoningCompletion(StateEvent stateEvent) =>
            stateEvent.EventData.Is(ReasoningCompletedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<ReasoningCompletedEvent>().Outcome ==
            HouseholdReasoningOutcome.Completed;
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public IReadOnlyList<StateEvent> EventsFor(string actorId) =>
            _events.TryGetValue(actorId, out var events)
                ? events.Select(stateEvent => stateEvent.Clone()).ToArray()
                : [];

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

            var appended = events.Select(stateEvent => stateEvent.Clone()).ToArray();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(stateEvent => stateEvent.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = EventsFor(agentId);
            return Task.FromResult<IReadOnlyList<StateEvent>>(fromVersion.HasValue
                ? events.Where(stateEvent => stateEvent.Version > fromVersion.Value).ToArray()
                : events);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = EventsFor(agentId);
            return Task.FromResult(events.Count == 0 ? 0 : events[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(stateEvent => stateEvent.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
