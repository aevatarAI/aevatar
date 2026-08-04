using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class RoleGAgentIncompleteSessionFinalizationLocalRuntimeTests
{
    [Theory]
    [InlineData(false, RoleChatSessionOutcome.Failed, "SESSION_ORPHANED")]
    [InlineData(true, RoleChatSessionOutcome.OutcomeUncertain, "SESSION_OUTCOME_UNCERTAIN")]
    public async Task Activation_ShouldRouteIncompleteSessionFinalizationThroughLocalActorInbox(
        bool hasCommittedProgress,
        RoleChatSessionOutcome expectedOutcome,
        string expectedFailureCode)
    {
        var actorId = $"role-incomplete-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var eventStore = new TerminalCommitSignalingEventStore(actorId, sessionId);
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.Replace(ServiceDescriptor.Singleton<IEventStore>(eventStore));
        services.AddSingleton<IAgentToolExecutionPort>(
            WorkflowGAgentTestBase.UnexpectedAgentToolExecutionPort.Instance);
        services.AddAevatarAgentKindRegistry(builder => builder.Register<RoleGAgent>());
        await using var serviceProvider = services.BuildServiceProvider();
        var finalizationSignalPublished =
            new TaskCompletionSource<RoleChatIncompleteSessionFinalizationRequested>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var actorStream = serviceProvider.GetRequiredService<IStreamProvider>().GetStream(actorId);
        await using var signalSubscription = await actorStream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(RoleChatIncompleteSessionFinalizationRequested.Descriptor) == true &&
                envelope.Route.GetTopologyAudience() == TopologyAudience.Self &&
                string.Equals(envelope.Route.PublisherActorId, actorId, StringComparison.Ordinal))
            {
                finalizationSignalPublished.TrySetResult(
                    envelope.Payload.Unpack<RoleChatIncompleteSessionFinalizationRequested>());
            }

            return Task.CompletedTask;
        });

        var seedEvents = new List<IMessage>
        {
            new RoleChatSessionStartedEvent
            {
                SessionId = sessionId,
                Prompt = "Recover this interrupted turn.",
            },
        };
        if (hasCommittedProgress)
        {
            seedEvents.Add(new RoleChatSessionProgressedEvent
            {
                SessionId = sessionId,
                Sequence = 1,
                TextStarted = new RoleChatTextStartedProgress { AgentId = actorId },
            });
        }

        await eventStore.AppendSeedAsync(actorId, seedEvents);

        var runtime = serviceProvider.GetRequiredService<IActorRuntime>();
        var actor = await runtime.CreateAsync<RoleGAgent>(actorId);
        var finalizationSignal = await finalizationSignalPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await eventStore.TerminalCommitted.WaitAsync(TimeSpan.FromSeconds(5));

        actor.Agent.Should().BeOfType<RoleGAgent>();
        finalizationSignal.SessionId.Should().Be(sessionId);
        finalizationSignal.ExpectedLastProgressSequence.Should().Be(hasCommittedProgress ? 1 : 0);
        completion.SessionId.Should().Be(sessionId);
        completion.Outcome.Should().Be(expectedOutcome);
        completion.FailureCode.Should().Be(expectedFailureCode);
        completion.TerminalProgress.Should().ContainSingle(progress =>
            progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal &&
            progress.Terminal.Outcome == expectedOutcome &&
            progress.Terminal.FailureCode == expectedFailureCode);
        eventStore.TerminalCommitExpectedVersion.Should().Be(seedEvents.Count);
    }

    private sealed class TerminalCommitSignalingEventStore(
        string actorId,
        string sessionId) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly TaskCompletionSource<RoleChatSessionCompletedEvent> _terminalCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RoleChatSessionCompletedEvent> TerminalCommitted => _terminalCommitted.Task;

        public long TerminalCommitExpectedVersion { get; private set; } = -1;

        public async Task AppendSeedAsync(
            string targetActorId,
            IReadOnlyList<IMessage> events,
            CancellationToken ct = default)
        {
            var stateEvents = events.Select((evt, index) => new StateEvent
            {
                EventId = $"seed-{index + 1}",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = index + 1,
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                AgentId = targetActorId,
            });
            await AppendAsync(targetActorId, stateEvents, expectedVersion: 0, ct);
        }

        public async Task<EventStoreCommitResult> AppendAsync(
            string targetActorId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var pending = events.Select(static evt => evt.Clone()).ToArray();
            var result = await _inner.AppendAsync(targetActorId, pending, expectedVersion, ct);
            if (string.Equals(targetActorId, actorId, StringComparison.Ordinal))
            {
                var completion = pending
                    .Where(evt => evt.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) == true)
                    .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
                    .SingleOrDefault(evt => string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal));
                if (completion != null)
                {
                    TerminalCommitExpectedVersion = expectedVersion;
                    _terminalCommitted.TrySetResult(completion.Clone());
                }
            }

            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string targetActorId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(targetActorId, fromVersion, ct);

        public Task<long> GetVersionAsync(string targetActorId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(targetActorId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string targetActorId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(targetActorId, toVersion, ct);
    }
}
