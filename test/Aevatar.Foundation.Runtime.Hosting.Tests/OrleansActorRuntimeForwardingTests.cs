using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorRuntimeForwardingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkAsync_WhenFleetAuthorityIsEitherEndpoint_ShouldReject(bool authorityIsParent)
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        var parentId = authorityIsParent
            ? RuntimeFleetCapabilityAuthorityIdentity.ActorId
            : "ordinary-parent";
        var childId = authorityIsParent
            ? "ordinary-child"
            : RuntimeFleetCapabilityAuthorityIdentity.ActorId;

        var act = () => runtime.LinkAsync(parentId, childId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot participate in actor hierarchy links*");
        grains.Should().BeEmpty();
        (await registry.ListBySourceAsync(parentId, CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync(childId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateByKindAsync_WithExplicitId_ShouldInitializeTrimmedKindAndReturnOrleansActor()
    {
        var runtime = CreateRuntime(out _, out var grains, out _);

        var actor = await runtime.CreateByKindAsync("  workflow.role-agent  ", "role:assistant");

        actor.Should().BeOfType<OrleansActor>();
        actor.Id.Should().Be("role:assistant");
        grains.Should().ContainKey("role:assistant");
        grains["role:assistant"].InitializedKinds.Should()
            .ContainSingle()
            .Which.Should().Be("workflow.role-agent");
    }

    [Fact]
    public async Task CreateByKindAsync_WhenInitializationFails_ShouldThrow()
    {
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializeAgentByKindResult = false;

        var act = () => runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to initialize Orleans actor role:assistant for kind 'workflow.role-agent'.*");
        grains["role:assistant"].InitializedKinds.Should()
            .ContainSingle()
            .Which.Should().Be("workflow.role-agent");
    }

    [Fact]
    public async Task CreateByKindAsync_WhenOrleansRejectsStaleSiloRoute_ShouldRetry()
    {
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.Zero);
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializationExceptions.Enqueue(
            CreateMessageRejectionException());

        var actor = await runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        actor.Id.Should().Be("role:assistant");
        grains["role:assistant"].InitializedKinds.Should().Equal(
            "workflow.role-agent",
            "workflow.role-agent");
    }

    [Fact]
    public async Task CreateByKindAsync_WhenTargetSiloIsTemporarilyUnavailable_ShouldRetry()
    {
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.Zero);
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializationExceptions.Enqueue(
            CreateSiloUnavailableException());

        var actor = await runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        actor.Id.Should().Be("role:assistant");
        grains["role:assistant"].InitializedKinds.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateByKindAsync_WhenOrleansDirectoryIsConverging_ShouldRetry()
    {
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.Zero);
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializationExceptions.Enqueue(
            CreateDirectoryConvergenceException());

        var actor = await runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        actor.Id.Should().Be("role:assistant");
        grains["role:assistant"].InitializedKinds.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateByKindAsync_WhenTopologyDoesNotConverge_ShouldStopAfterAttemptLimit()
    {
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.Zero);
        await runtime.ExistsAsync("role:assistant");
        for (var i = 0; i < 3; i++)
            grains["role:assistant"].InitializationExceptions.Enqueue(
                CreateSiloUnavailableException());

        var act = () => runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        await act.Should().ThrowAsync<SiloUnavailableException>();
        grains["role:assistant"].InitializedKinds.Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateByKindAsync_WhenFailureIsNotTopologyConvergence_ShouldNotRetry()
    {
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.Zero);
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializationExceptions.Enqueue(
            new InvalidOperationException("initialization failure"));

        var act = () => runtime.CreateByKindAsync("workflow.role-agent", "role:assistant");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("initialization failure");
        grains["role:assistant"].InitializedKinds.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateByKindAsync_WhenTopologyRetryIsCancelled_ShouldStopWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = CreateRuntime(
            out _,
            out var grains,
            out _,
            initializationAttemptLimit: 3,
            initializationRetryDelay: TimeSpan.FromMinutes(1));
        await runtime.ExistsAsync("role:assistant");
        grains["role:assistant"].InitializationExceptions.Enqueue(
            CreateMessageRejectionException());
        grains["role:assistant"].OnInitialize = cancellation.Cancel;

        var act = () => runtime.CreateByKindAsync(
            "workflow.role-agent",
            "role:assistant",
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        grains["role:assistant"].InitializedKinds.Should().ContainSingle();
    }

    [Fact]
    public async Task LinkAsync_ShouldRegisterForwardingBinding_AndUpdateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);

        await runtime.LinkAsync("parent", "child");

        grains["parent"].AddChildCallCount.Should().Be(1);
        grains["parent"].Children.Should().Contain("child");
        grains["child"].ParentId.Should().Be("parent");
        var parentBindings = await registry.ListBySourceAsync("parent", CancellationToken.None);
        var hierarchyBinding = parentBindings.Should().ContainSingle(x => x.TargetStreamId == "child").Subject;
        hierarchyBinding.ForwardingMode.Should().Be(StreamForwardingMode.HandleThenForward);
        hierarchyBinding.DirectionFilter.SetEquals([TopologyAudience.Children, TopologyAudience.ParentAndChildren]).Should().BeTrue();

        var childBindings = await registry.ListBySourceAsync("child", CancellationToken.None);
        var committedObservationBinding = childBindings.Should().ContainSingle(x => x.TargetStreamId == "parent").Subject;
        committedObservationBinding.ForwardingMode.Should().Be(StreamForwardingMode.HandleThenForward);
        committedObservationBinding.DirectionFilter.SetEquals([TopologyAudience.Unspecified]).Should().BeTrue();
        committedObservationBinding.EventTypeFilter.Should().ContainSingle()
            .Which.Should().Be($"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}");
    }

    [Fact]
    public async Task LinkAsync_WithBoundCurrentParent_ShouldUseParentGrainWithoutTouchingBoundState()
    {
        var stateBindingAccessor = new AsyncLocalRuntimeActorStateBindingAccessor();
        var runtime = CreateRuntime(out _, out var grains, out _);
        var boundState = CreatePersistentState("parent");
        var publicationState = CreatePublicationState();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)boundState;

        using (stateBindingAccessor.Bind(boundState, publicationState))
            await runtime.LinkAsync("parent", "child");

        stateProxy.State.Children.Should().BeEmpty();
        stateProxy.WriteCount.Should().Be(0);
        grains["parent"].AddChildCallCount.Should().Be(1);
        grains["parent"].Children.Should().ContainSingle("child");
        grains["child"].ParentId.Should().Be("parent");
    }

    [Fact]
    public async Task LinkAsync_WithBoundCurrentParent_WhenRepeated_ShouldLeaveTopologyIdempotent()
    {
        var stateBindingAccessor = new AsyncLocalRuntimeActorStateBindingAccessor();
        var runtime = CreateRuntime(out _, out var grains, out _);
        var boundState = CreatePersistentState("parent");
        var publicationState = CreatePublicationState();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)boundState;

        using (stateBindingAccessor.Bind(boundState, publicationState))
        {
            await runtime.LinkAsync("parent", "child");
            await runtime.LinkAsync("parent", "child");
        }

        stateProxy.State.Children.Should().BeEmpty();
        stateProxy.WriteCount.Should().Be(0);
        grains["parent"].Children.Should().ContainSingle("child");
        grains["parent"].AddChildCallCount.Should().Be(2);
        grains["child"].ParentId.Should().Be("parent");
    }

    [Fact]
    public async Task LinkAsync_ShouldForwardChildCommittedFactsObserverPublicationsToParent()
    {
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.LinkAsync("parent", "child");

        var committed = new EventEnvelope
        {
            Id = "committed-facts-publication",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "child",
                    EventId = "event-1",
                    Version = 1,
                    EventType = "test.committed",
                    EventData = Any.Pack(new StringValue { Value = "done" }),
                },
            }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("child", ObserverAudience.CommittedFacts),
        };

        var parentReceived = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        await grains["parent"].SubscribeOwnStreamAsync(envelope =>
        {
            if (string.Equals(envelope.Id, "committed-facts-publication", StringComparison.Ordinal))
                parentReceived.TrySetResult(envelope);

            return Task.CompletedTask;
        }, CancellationToken.None);

        await grains["child"].PublishToOwnStreamAsync(committed, CancellationToken.None);

        var forwarded = await parentReceived.Task;
        forwarded.Route!.IsObserverPublication().Should().BeTrue();
        forwarded.Route.GetObserverAudience().Should().Be(ObserverAudience.CommittedFacts);
        forwarded.Payload!.Unpack<CommittedStateEventPublished>()
            .StateEvent.EventData.Unpack<StringValue>().Value.Should().Be("done");
        StreamForwardingEnvelopeState.GetSourceStreamId(forwarded).Should().Be("child");
        StreamForwardingEnvelopeState.GetTargetStreamId(forwarded).Should().Be("parent");
    }

    [Fact]
    public async Task UnlinkAsync_ShouldRemoveForwardingBinding_AndTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.LinkAsync("parent", "child");

        await runtime.UnlinkAsync("child");

        grains["parent"].Children.Should().NotContain("child");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("child", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LinkAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);

        await runtime.LinkAsync("parent", "child");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task UnlinkAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.LinkAsync("parent", "child");
        grains["parent"].ObservedReentrancyIds.Clear();
        grains["child"].ObservedReentrancyIds.Clear();

        await runtime.UnlinkAsync("child");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task DestroyAsync_ShouldCleanupIncomingAndOutgoingForwardingBindings()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.LinkAsync("parent", "middle");
        await runtime.LinkAsync("middle", "child-1");
        await runtime.LinkAsync("middle", "child-2");

        await runtime.DestroyAsync("middle");

        grains["parent"].Children.Should().NotContain("middle");
        grains["child-1"].ParentId.Should().BeNull();
        grains["child-2"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("middle", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("child-1", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("child-2", CancellationToken.None)).Should().BeEmpty();
        grains["middle"].Calls.Should().ContainInOrder("Purge", "Deactivate");
    }

    [Fact]
    public async Task LinkAsync_WhenChildIsNotInitialized_ShouldThrow_AndNotMutateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.ExistsAsync("parent");
        await runtime.ExistsAsync("child");
        grains["child"].Initialized = false;

        var act = () => runtime.LinkAsync("parent", "child");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Child actor child is not initialized.*");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LinkAsync_WhenParentIsNotInitialized_ShouldStillLink_AndSkipParentInitializationProbe()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.ExistsAsync("parent");
        await runtime.ExistsAsync("child");
        grains["parent"].Initialized = false;

        await runtime.LinkAsync("parent", "child");

        grains["parent"].Children.Should().Contain("child");
        grains["child"].ParentId.Should().Be("parent");
        grains["parent"].IsInitializedCallCount.Should().Be(1);
        grains["child"].IsInitializedCallCount.Should().Be(2);
        (await registry.ListBySourceAsync("parent", CancellationToken.None))
            .Should().ContainSingle(x => x.TargetStreamId == "child");
        (await registry.ListBySourceAsync("child", CancellationToken.None))
            .Should().ContainSingle(x => x.TargetStreamId == "parent");
    }

    [Fact]
    public async Task DestroyAsync_ShouldRemoveStreamFromLifecycleManager()
    {
        var lifecycleManager = new RecordingStreamLifecycleManager();
        var runtime = CreateRuntime(out _, out _, out _, lifecycleManager);

        await runtime.DestroyAsync("actor-1");

        lifecycleManager.RemovedStreamActorIds.Should().ContainSingle("actor-1");
    }

    [Fact]
    public async Task DestroyAsync_ShouldPurgeDurableCallbackSchedulerState()
    {
        var runtime = CreateRuntime(out _, out _, out var callbackSchedulerGrains);

        await runtime.DestroyAsync("actor-1");

        callbackSchedulerGrains["actor-1"].PurgeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DestroyAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.LinkAsync("parent", "middle");
        await runtime.LinkAsync("middle", "child");
        grains["parent"].ObservedReentrancyIds.Clear();
        grains["middle"].ObservedReentrancyIds.Clear();
        grains["child"].ObservedReentrancyIds.Clear();

        await runtime.DestroyAsync("middle");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["middle"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    private static OrleansActorRuntime CreateRuntime(
        out InMemoryStreamForwardingRegistry registry,
        out Dictionary<string, RecordingRuntimeActorGrain> grains,
        out Dictionary<string, RecordingCallbackSchedulerGrain> callbackSchedulerGrains,
        IStreamLifecycleManager? streamLifecycleManager = null,
        int initializationAttemptLimit = 30,
        TimeSpan? initializationRetryDelay = null)
    {
        var grainMap = new Dictionary<string, RecordingRuntimeActorGrain>(StringComparer.Ordinal);
        var callbackSchedulerGrainMap = new Dictionary<string, RecordingCallbackSchedulerGrain>(StringComparer.Ordinal);
        registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions { ThrowOnSubscriberError = true },
            NullLoggerFactory.Instance,
            registry);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).ResolveGrain = actorId =>
        {
            if (!grainMap.TryGetValue(actorId, out var grain))
            {
                grain = new RecordingRuntimeActorGrain(streams, actorId);
                grainMap[actorId] = grain;
            }

            return grain;
        };
        ((GrainFactoryProxy)(object)grainFactory).ResolveCallbackSchedulerGrain = actorId =>
        {
            if (!callbackSchedulerGrainMap.TryGetValue(actorId, out var grain))
            {
                grain = new RecordingCallbackSchedulerGrain();
                callbackSchedulerGrainMap[actorId] = grain;
            }

            return grain;
        };

        grains = grainMap;
        callbackSchedulerGrains = callbackSchedulerGrainMap;
        return new OrleansActorRuntime(
            grainFactory,
            streams,
            new OrleansActorRuntimeDurableCallbackScheduler(grainFactory),
            new AgentKindRegistry([]),
            streamLifecycleManager,
            NullLogger<OrleansActorRuntime>.Instance,
            initializationAttemptLimit,
            initializationRetryDelay ?? TimeSpan.FromSeconds(1));
    }

    private static OrleansMessageRejectionException CreateMessageRejectionException() =>
        (OrleansMessageRejectionException)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["stale silo route"],
            culture: null)!;

    private static SiloUnavailableException CreateSiloUnavailableException() =>
        (SiloUnavailableException)Activator.CreateInstance(
            typeof(SiloUnavailableException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["stale silo route"],
            culture: null)!;

    private static OrleansException CreateDirectoryConvergenceException() =>
        (OrleansException)Activator.CreateInstance(
            typeof(OrleansException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["Current directory is not stable to perform the lookup. Retry later."],
            culture: null)!;

    private static IPersistentState<RuntimeActorGrainState> CreatePersistentState(string actorId)
    {
        var persistentState = DispatchProxy.Create<
            IPersistentState<RuntimeActorGrainState>,
            RuntimeActorPersistentStateProxy>();
        ((RuntimeActorPersistentStateProxy)(object)persistentState).State.AgentId = actorId;
        return persistentState;
    }

    private static IPersistentState<RuntimeActorCommittedStatePublicationGrainState>
        CreatePublicationState() =>
        DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<string, IRuntimeActorGrain>? ResolveGrain { get; set; }

        public Func<string, IRuntimeCallbackSchedulerGrain>? ResolveCallbackSchedulerGrain { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeActorGrain) &&
                args is { Length: > 0 } &&
                args[0] is string actorId &&
                ResolveGrain != null)
            {
                return ResolveGrain(actorId);
            }

            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeCallbackSchedulerGrain) &&
                args is { Length: > 0 } &&
                args[0] is string callbackActorId &&
                ResolveCallbackSchedulerGrain != null)
            {
                return ResolveCallbackSchedulerGrain(callbackActorId);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;
        private readonly string _actorId;
        private IAsyncDisposable? _selfStreamSubscription;

        public RecordingRuntimeActorGrain(Aevatar.Foundation.Abstractions.IStreamProvider streams, string actorId)
        {
            _streams = streams;
            _actorId = actorId;
        }

        public string? ParentId { get; private set; }

        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);

        public bool Initialized { get; set; } = true;

        public bool InitializeAgentByKindResult { get; set; } = true;

        public Queue<Exception> InitializationExceptions { get; } = new();

        public Action? OnInitialize { get; set; }

        public List<string> Calls { get; } = [];
        public List<Guid> ObservedReentrancyIds { get; } = [];
        public List<string> InitializedKinds { get; } = [];
        public List<EventEnvelope> HandledEnvelopes { get; } = [];

        public int IsInitializedCallCount { get; private set; }
        public int AddChildCallCount { get; private set; }

        private async Task<bool> SubscribeSelfStreamOnceAsync()
        {
            if (_selfStreamSubscription == null)
            {
                _selfStreamSubscription = await _streams.GetStream(_actorId)
                    .SubscribeAsync<EventEnvelope>(envelope => HandleEnvelopeAsync(envelope.ToByteArray()));
            }

            return true;
        }

        public Task<bool> InitializeAgentByKindAsync(string kind)
        {
            InitializedKinds.Add(kind);
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);

            OnInitialize?.Invoke();
            if (InitializationExceptions.TryDequeue(out var exception))
                return Task.FromException<bool>(exception);

            return InitializeAgentByKindResult
                ? SubscribeSelfStreamOnceAsync()
                : Task.FromResult(false);
        }

        public Task<bool> IsInitializedAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            IsInitializedCallCount++;
            return Task.FromResult(Initialized);
        }

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task PublishToOwnStreamAsync(EventEnvelope envelope, CancellationToken ct) =>
            _streams.GetStream(_actorId).ProduceAsync(envelope, ct);

        public Task<IAsyncDisposable> SubscribeOwnStreamAsync(
            Func<EventEnvelope, Task> handler,
            CancellationToken ct) =>
            _streams.GetStream(_actorId).SubscribeAsync(handler, ct);

        public Task AddChildAsync(string childId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            AddChildCallCount++;
            Children.Add(childId);
            return Task.CompletedTask;
        }

        public Task RemoveChildAsync(string childId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            Children.Remove(childId);
            return Task.CompletedTask;
        }

        public Task SetParentAsync(string parentId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            ParentId = parentId;
            return Task.CompletedTask;
        }

        public Task ClearParentAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            ParentId = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetChildrenAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.FromResult<IReadOnlyList<string>>(Children.ToList());
        }

        public Task<string?> GetParentAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.FromResult(ParentId);
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("recording");

        public Task<string> GetAgentKindAsync() =>
            Task.FromResult(string.Empty);

        public Task DeactivateAsync()
        {
            Calls.Add("Deactivate");
            return Task.CompletedTask;
        }

        public Task PurgeAsync()
        {
            Calls.Add("Purge");
            ParentId = null;
            Children.Clear();
            return Task.CompletedTask;
        }
    }

    private class RuntimeActorPersistentStateProxy : DispatchProxy
    {
        public RuntimeActorGrainState State { get; set; } = new();

        public int WriteCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name;
            if (name == "get_State")
                return State;
            if (name == "set_State")
            {
                State = args?[0] as RuntimeActorGrainState ?? new RuntimeActorGrainState();
                return null;
            }

            if (name == "WriteStateAsync")
            {
                WriteCount++;
                return Task.CompletedTask;
            }

            if (name == "ReadStateAsync" || name == "ClearStateAsync")
                return Task.CompletedTask;
            if (name == "get_RecordExists")
                return true;
            if (name == "get_Etag")
                return string.Empty;
            if (name == "set_Etag")
                return null;

            return targetMethod?.ReturnType?.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private class CommittedStatePublicationPersistentStateProxy : DispatchProxy
    {
        public RuntimeActorCommittedStatePublicationGrainState State { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name;
            if (name == "get_State")
                return State;
            if (name == "set_State")
            {
                State = args?[0] as RuntimeActorCommittedStatePublicationGrainState ?? new();
                return null;
            }
            if (name is "WriteStateAsync" or "ReadStateAsync" or "ClearStateAsync")
                return Task.CompletedTask;
            if (name == "get_RecordExists")
                return true;
            if (name == "get_Etag")
                return string.Empty;
            if (name == "set_Etag")
                return null;

            return targetMethod?.ReturnType?.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class RecordingStreamLifecycleManager : IStreamLifecycleManager
    {
        public List<string> RemovedStreamActorIds { get; } = [];

        public void RemoveStream(string actorId)
        {
            RemovedStreamActorIds.Add(actorId);
        }
    }

    private sealed class RecordingCallbackSchedulerGrain : IRuntimeCallbackSchedulerGrain
    {
        public int PurgeCalls { get; private set; }

        public Task<long> ScheduleTimeoutAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            int dueTimeMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = triggerEnvelope;
            _ = dueTimeMs;
            _ = deliveryMode;
            throw new NotSupportedException();
        }

        public Task<long> ScheduleCoalescedTimeoutAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            int dueTimeMs,
            string coalescingKey,
            long coalescingSequence,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = triggerEnvelope;
            _ = dueTimeMs;
            _ = coalescingKey;
            _ = coalescingSequence;
            _ = deliveryMode;
            throw new NotSupportedException();
        }

        public Task<long> ScheduleTimerAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            int dueTimeMs,
            int periodMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = triggerEnvelope;
            _ = dueTimeMs;
            _ = periodMs;
            _ = deliveryMode;
            throw new NotSupportedException();
        }

        public Task CancelAsync(
            string callbackId,
            long expectedGeneration = 0,
            int expectedSlotEpoch = RuntimeCallbackSlotEpoch.Unspecified)
        {
            _ = callbackId;
            _ = expectedGeneration;
            _ = expectedSlotEpoch;
            return Task.CompletedTask;
        }

        public Task PurgeAsync()
        {
            PurgeCalls++;
            return Task.CompletedTask;
        }
    }
}
