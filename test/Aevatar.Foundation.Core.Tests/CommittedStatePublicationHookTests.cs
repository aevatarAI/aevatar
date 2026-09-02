using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public sealed class CommittedStatePublicationHookTests
{
    [Fact]
    public async Task Hooks_ShouldRunBeforeCommittedObserverPublication()
    {
        var hook = new RecordingPublicationHook();
        var publisher = new RecordingCommittedPublisher(hook);
        var agent = CreateAgent(hook, publisher);
        agent.SetId("hook-order-agent");
        await agent.ActivateAsync();

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 3 }));

        hook.Contexts.Should().ContainSingle();
        publisher.Publications.Should().ContainSingle();
        hook.InvocationOrder.Should().Be(1);
        publisher.Publications[0].order.Should().Be(2);
    }

    [Fact]
    public async Task Context_ShouldIncludeActorIdentityCommittedPayloadAndSourceEnvelope()
    {
        var hook = new RecordingPublicationHook();
        var publisher = new RecordingCommittedPublisher(hook);
        var agent = CreateAgent(hook, publisher);
        agent.SetId("hook-context-agent");
        await agent.ActivateAsync();
        var inbound = TestHelper.Envelope(new IncrementEvent { Amount = 4 }, "caller");

        await agent.HandleEventAsync(inbound);

        var context = hook.Contexts.Should().ContainSingle().Subject;
        context.ActorId.Should().Be("hook-context-agent");
        context.ActorType.Should().Be(typeof(HookCounterAgent));
        context.SourceEnvelope.Should().BeSameAs(inbound);
        context.Audience.Should().Be(ObserverAudience.CommittedFacts);
        context.Published.StateEvent.Should().NotBeNull();
        context.Published.StateEvent.AgentId.Should().Be("hook-context-agent");
        context.Published.StateEvent.EventData.Unpack<IncrementEvent>().Amount.Should().Be(4);
        context.Published.StateRoot.Should().NotBeNull();
        context.Published.StateRoot.Unpack<CounterState>().Count.Should().Be(4);
        publisher.Publications[0].published.Should().BeSameAs(context.Published);
    }

    [Fact]
    public async Task MissingOptionalHookCollection_ShouldStillPublishCommittedObservation()
    {
        var publisher = new RecordingCommittedPublisher();
        var agent = CreateAgentWithoutHooks(publisher);
        agent.SetId("no-hook-agent");
        await agent.ActivateAsync();

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 2 }));

        publisher.Publications.Should().ContainSingle();
        publisher.Publications[0].published.StateEvent.AgentId.Should().Be("no-hook-agent");
        publisher.Publications[0].published.StateRoot.Unpack<CounterState>().Count.Should().Be(2);
    }

    [Fact]
    public async Task PostCommitStateChangeFailure_ShouldNotSkipCommittedPublication()
    {
        var publisher = new RecordingCommittedPublisher();
        var agent = CreateFailingPostCommitAgent(publisher);
        agent.SetId("post-commit-failure-agent");
        await agent.ActivateAsync();

        var act = () => agent.HandleEventAsync(
            TestHelper.Envelope(new IncrementEvent { Amount = 5 }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("post-commit refresh failed");
        agent.State.Count.Should().Be(5);
        publisher.Publications.Should().ContainSingle();
        publisher.Publications[0].published.StateEvent.EventData
            .Unpack<IncrementEvent>().Amount.Should().Be(5);
        publisher.Publications[0].published.StateRoot
            .Unpack<CounterState>().Count.Should().Be(5);
    }

    private static HookCounterAgent CreateAgent(
        RecordingPublicationHook hook,
        RecordingCommittedPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .AddSingleton<ICommittedStatePublicationHook>(hook)
            .BuildServiceProvider();

        return new HookCounterAgent
        {
            Services = services,
            CommittedStateEventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
    }

    private static HookCounterAgent CreateAgentWithoutHooks(RecordingCommittedPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .BuildServiceProvider();

        return new HookCounterAgent
        {
            Services = services,
            CommittedStateEventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
    }

    private static FailingPostCommitHookCounterAgent CreateFailingPostCommitAgent(
        RecordingCommittedPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddRuntimeScheduler()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IStateEventApplier<CounterState>, CounterIncrementApplier>()
            .BuildServiceProvider();

        return new FailingPostCommitHookCounterAgent
        {
            Services = services,
            CommittedStateEventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>(),
        };
    }

    private sealed class HookCounterAgent : TestGAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandleIncrement(IncrementEvent evt) => PersistDomainEventAsync(evt);
    }

    private sealed class FailingPostCommitHookCounterAgent : TestGAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandleIncrement(IncrementEvent evt) => PersistDomainEventAsync(evt);

        protected override Task OnCommittedStateChangedAsync(
            CounterState state,
            CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("post-commit refresh failed"));
    }

    private sealed class CounterIncrementApplier
        : StateEventApplierBase<CounterState, IncrementEvent>
    {
        protected override CounterState Apply(CounterState current, IncrementEvent evt) =>
            new()
            {
                Count = current.Count + evt.Amount,
                Name = current.Name,
            };
    }

    private sealed class RecordingPublicationHook : ICommittedStatePublicationHook
    {
        private int _sequence;

        public List<CommittedStatePublicationContext> Contexts { get; } = [];

        public int InvocationOrder { get; private set; }

        public int NextOrder() => ++_sequence;

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InvocationOrder = NextOrder();
            Contexts.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommittedPublisher(RecordingPublicationHook? hook = null) : ICommittedStateEventPublisher
    {
        public List<(CommittedStateEventPublished published, ObserverAudience audience, EventEnvelope? source, int order)> Publications { get; } = [];

        public Task PublishAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = options;
            ct.ThrowIfCancellationRequested();
            Publications.Add((evt, audience, sourceEnvelope, hook?.NextOrder() ?? 0));
            return Task.CompletedTask;
        }
    }
}
