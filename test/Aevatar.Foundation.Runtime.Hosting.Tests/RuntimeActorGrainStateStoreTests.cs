using System.Reflection;
using Aevatar.GAgents.Scheduled;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeActorGrainStateStoreTests
{
    private const string LegacyUserAgentCatalogStateClrName =
        "Aevatar.GAgents.ChannelRuntime." + "Agent" + "Registry" + "State";

    [Fact]
    public async Task RuntimeActorGrainStateStore_ShouldRoundtripProtobufState()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var store = new RuntimeActorGrainStateStore<EventEnvelope>(runtimeState);
        var state = new EventEnvelope
        {
            Id = "evt-1",
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("publisher-1", TopologyAudience.ParentAndChildren),
            Propagation = new EnvelopePropagation
            {
                Baggage =
                {
                    ["k"] = "v",
                },
            },
        };

        await store.SaveAsync("actor-1", state);
        var loaded = await store.LoadAsync("actor-1");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("evt-1");
        loaded.Route!.PublisherActorId.Should().Be("publisher-1");
        loaded.Route.GetTopologyAudience().Should().Be(TopologyAudience.ParentAndChildren);
        loaded.Propagation!.Baggage.Should().ContainKey("k").WhoseValue.Should().Be("v");
        stateProxy.State.AgentStateTypeName.Should().Be(typeof(EventEnvelope).FullName);
        stateProxy.State.AgentStateSnapshot.Should().NotBeNull();
        stateProxy.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeActorGrainStateStore_WhenSnapshotTypeIsDifferent_ShouldReturnNull()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var stored = new EventEnvelope
        {
            Id = "evt-snapshot",
        };
        stateProxy.State.AgentStateTypeName = typeof(EventEnvelope).FullName;
        stateProxy.State.AgentStateSnapshot = stored.ToByteArray();
        var store = new RuntimeActorGrainStateStore<ParentChangedEvent>(runtimeState);

        var loaded = await store.LoadAsync("actor-1");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task RuntimeActorGrainStateStore_ShouldLoadLegacyClrTypeName_ForRenamedUserAgentCatalogState()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var stored = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-compat-1",
                    AgentType = "skill_runner",
                    TemplateName = "summary",
                },
            },
        };
        stateProxy.State.AgentStateTypeName = LegacyUserAgentCatalogStateClrName;
        stateProxy.State.AgentStateSnapshot = stored.ToByteArray();
        var store = new RuntimeActorGrainStateStore<UserAgentCatalogState>(runtimeState);

        var loaded = await store.LoadAsync("actor-1");

        loaded.Should().NotBeNull();
        loaded!.Entries.Should().ContainSingle(x => x.AgentId == "agent-compat-1");
    }

    [Fact]
    public async Task RuntimeActorGrainEventSourcingSnapshotStore_ShouldLoadLegacyClrTypeName_ForRenamedUserAgentCatalogState()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var stored = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-compat-2",
                    AgentType = "workflow_agent",
                    TemplateName = "workflow_agent",
                },
            },
        };
        stateProxy.State.AgentStateTypeName = LegacyUserAgentCatalogStateClrName;
        stateProxy.State.AgentStateSnapshot = stored.ToByteArray();
        stateProxy.State.AgentStateSnapshotVersion = 17;
        var store = new RuntimeActorGrainEventSourcingSnapshotStore<UserAgentCatalogState>(runtimeState);

        var loaded = await store.LoadAsync("actor-1");

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(17);
        loaded.State.Entries.Should().ContainSingle(x => x.AgentId == "agent-compat-2");
    }

    [Fact]
    public async Task RuntimeActorGrainStateStore_Delete_ShouldClearSnapshot()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        stateProxy.State.AgentStateTypeName = typeof(EventEnvelope).FullName;
        stateProxy.State.AgentStateSnapshot = new EventEnvelope
        {
            Id = "evt-to-delete",
        }.ToByteArray();
        var store = new RuntimeActorGrainStateStore<EventEnvelope>(runtimeState);

        await store.DeleteAsync("actor-1");

        stateProxy.State.AgentStateTypeName.Should().BeNull();
        stateProxy.State.AgentStateSnapshot.Should().BeNull();
        stateProxy.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_ShouldWriteDedicatedRowWithoutRewritingBusinessSnapshot()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = "actor-publication";
        stateProxy.State.AgentStateSnapshot = new byte[] { 1, 2, 3 };
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        await store.InitializeAsync("actor-publication", 3);
        await store.RecordFailureAsync(
            "actor-publication",
            3,
            new StateEvent
            {
                AgentId = "actor-publication",
                EventId = "event-4",
                Version = 4,
            },
            CommittedStatePublicationFailureStage.AdapterAcceptance,
            new InvalidOperationException("injected"));
        var advanced = await store.AdvanceAsync(
            "actor-publication",
            3,
            new StateEvent
            {
                AgentId = "actor-publication",
                EventId = "event-4",
                Version = 4,
            });

        advanced.PublishedVersion.Should().Be(4);
        advanced.PublishedEventId.Should().Be("event-4");
        advanced.Failure.Should().BeNull();
        stateProxy.State.AgentStateSnapshot.Should().Equal(1, 2, 3);
        stateProxy.WriteCount.Should().Be(0);
        var persisted = CommittedStatePublicationState.Parser.ParseFrom(
            publicationProxy.State.Checkpoint);
        persisted.Should().BeEquivalentTo(advanced);
        CommittedStatePublicationState.Parser.ParseFrom(
                stateProxy.State.CommittedStatePublicationState)
            .Should().BeEquivalentTo(advanced, "the next ordinary runtime-row write should carry a rollback shadow");
        publicationProxy.WriteCount.Should().Be(3);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenWriteCommitsBeforeError_ShouldConfirmByReadBack()
    {
        const string actorId = "actor-publication-unknown-write";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        publicationProxy.CommitThenThrowOnNextWrite = true;
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var initialized = await store.InitializeAsync(actorId, 4);

        initialized.PublishedVersion.Should().Be(4);
        publicationProxy.WriteCount.Should().Be(1);
        publicationProxy.ReadCount.Should().Be(1);
        CommittedStatePublicationState.Parser.ParseFrom(publicationProxy.State.Checkpoint)
            .Should().BeEquivalentTo(initialized);
        CommittedStatePublicationState.Parser.ParseFrom(stateProxy.State.CommittedStatePublicationState)
            .Should().BeEquivalentTo(initialized);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenWriteFailsBeforeCommit_ShouldPropagate()
    {
        const string actorId = "actor-publication-failed-write";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        publicationProxy.FailBeforeCommitOnNextWrite = true;
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var act = () => store.InitializeAsync(actorId, 4);

        await act.Should().ThrowAsync<IOException>()
            .WithMessage("synthetic publication write failure before commit");
        publicationProxy.WriteCount.Should().Be(1);
        publicationProxy.ReadCount.Should().Be(1);
        publicationProxy.State.Checkpoint.Should().BeNull();
        stateProxy.State.CommittedStatePublicationState.Should().BeNull();
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WithLegacyOnlyCheckpoint_ShouldMigrateToDedicatedRow()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = "actor-legacy-publication";
        var legacy = BuildPublicationCheckpoint(
            "actor-legacy-publication",
            publishedVersion: 7,
            publishedEventId: "event-7",
            revision: 9);
        stateProxy.State.CommittedStatePublicationState = legacy.ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var loaded = await store.LoadAsync("actor-legacy-publication");

        loaded.Should().BeEquivalentTo(legacy);
        CommittedStatePublicationState.Parser.ParseFrom(publicationProxy.State.Checkpoint)
            .Should().BeEquivalentTo(legacy);
        publicationProxy.WriteCount.Should().Be(1);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeActorPublicationStateStore_ShouldSelectHigherPublishedVersion(
        bool legacyIsNewer)
    {
        const string actorId = "actor-split-publication";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        var older = BuildPublicationCheckpoint(actorId, 3, "event-3", revision: 20);
        var newer = BuildPublicationCheckpoint(actorId, 4, "event-4", revision: 1);
        stateProxy.State.CommittedStatePublicationState = (legacyIsNewer ? newer : older).ToByteArray();
        publicationProxy.State.Checkpoint = (legacyIsNewer ? older : newer).ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var loaded = await store.LoadAsync(actorId);

        loaded.Should().BeEquivalentTo(newer);
        CommittedStatePublicationState.Parser.ParseFrom(publicationProxy.State.Checkpoint)
            .Should().BeEquivalentTo(newer);
        publicationProxy.WriteCount.Should().Be(legacyIsNewer ? 1 : 0);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_ShouldSelectHigherRevisionAtSamePublishedVersion()
    {
        const string actorId = "actor-split-publication-revision";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        stateProxy.State.CommittedStatePublicationState =
            BuildPublicationCheckpoint(actorId, 5, "event-5", revision: 3).ToByteArray();
        var expected = BuildPublicationCheckpoint(actorId, 5, "event-5", revision: 4);
        expected.Failure = new CommittedStatePublicationFailure
        {
            Version = 6,
            EventId = "event-6",
            Attempts = 1,
            Stage = CommittedStatePublicationFailureStage.AdapterAcceptance,
        };
        publicationProxy.State.Checkpoint = expected.ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var loaded = await store.LoadAsync(actorId);

        loaded.Should().BeEquivalentTo(expected);
        publicationProxy.WriteCount.Should().Be(0);
        CommittedStatePublicationState.Parser.ParseFrom(
                stateProxy.State.CommittedStatePublicationState)
            .Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenEqualVersionEventIdsConflict_ShouldFailClosed()
    {
        const string actorId = "actor-conflicting-publication";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        stateProxy.State.CommittedStatePublicationState =
            BuildPublicationCheckpoint(actorId, 6, "event-a", revision: 7).ToByteArray();
        publicationProxy.State.Checkpoint =
            BuildPublicationCheckpoint(actorId, 6, "event-b", revision: 8).ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var act = () => store.LoadAsync(actorId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*disagree*published version 6*");
        publicationProxy.WriteCount.Should().Be(0);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenEqualRevisionTelemetryDiverges_ShouldConvergeOnDedicatedRow()
    {
        const string actorId = "actor-conflicting-publication-payload";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        var legacy = BuildPublicationCheckpoint(actorId, 6, "event-6", revision: 8);
        legacy.Failure = new CommittedStatePublicationFailure
        {
            Version = 7,
            EventId = "event-7",
            Attempts = 1,
            Stage = CommittedStatePublicationFailureStage.AdapterAcceptance,
        };
        var dedicated = legacy.Clone();
        dedicated.Failure.Attempts = 2;
        stateProxy.State.CommittedStatePublicationState = legacy.ToByteArray();
        publicationProxy.State.Checkpoint = dedicated.ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var loaded = await store.LoadAsync(actorId);

        loaded.Should().BeEquivalentTo(
            dedicated,
            "authoritative progress already matches, so only advisory failure telemetry diverged "
            + "and the dedicated row is the authoritative write target");
        CommittedStatePublicationState.Parser.ParseFrom(stateProxy.State.CommittedStatePublicationState)
            .Should().BeEquivalentTo(dedicated, "the rollback shadow is repaired in place");
        publicationProxy.WriteCount.Should().Be(0);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenEqualRevisionAuthoritativeFieldsDisagree_ShouldStillFailClosed()
    {
        const string actorId = "actor-conflicting-publication-authority";
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = actorId;
        var legacy = BuildPublicationCheckpoint(actorId, 6, "event-6", revision: 8);
        var dedicated = BuildPublicationCheckpoint(actorId, 6, "event-6-other", revision: 8);
        stateProxy.State.CommittedStatePublicationState = legacy.ToByteArray();
        publicationProxy.State.Checkpoint = dedicated.ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var act = () => store.LoadAsync(actorId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*disagree*published version 6*");
        publicationProxy.WriteCount.Should().Be(0);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeActorPublicationStateStore_WhenStoredActorDiffers_ShouldFailClosed()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = "actor-requested";
        publicationProxy.State.Checkpoint =
            BuildPublicationCheckpoint("actor-other", 2, "event-2", revision: 2).ToByteArray();
        var store = new RuntimeActorGrainCommittedStatePublicationStateStore(runtimeState, publicationState);

        var act = () => store.LoadAsync("actor-requested");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*belongs to actor 'actor-other', not 'actor-requested'*");
    }

    [Fact]
    public async Task PublicationStateResolution_WithBoundRuntimeState_ShouldUseActorOwnedStore()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var publicationProxy = (CommittedStatePublicationPersistentStateProxy)(object)publicationState;
        stateProxy.State.AgentId = "actor-publication-di";
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IRuntimeActorStateBindingAccessor>();

        using (accessor.Bind(runtimeState, publicationState))
        {
            var store = provider.GetRequiredService<ICommittedStatePublicationStateStore>();
            await store.InitializeAsync("actor-publication-di", 7);
        }

        var persisted = CommittedStatePublicationState.Parser.ParseFrom(
            publicationProxy.State.Checkpoint);
        persisted.PublishedVersion.Should().Be(7);
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task IStateStoreResolution_WithBoundRuntimeState_ShouldUseRuntimeActorStateStore()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var publicationState = DispatchProxy.Create<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>,
            CommittedStatePublicationPersistentStateProxy>();
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IRuntimeActorStateBindingAccessor>();

        IStateStore<EventEnvelope> store;
        using (accessor.Bind(runtimeState, publicationState))
        {
            store = provider.GetRequiredService<IStateStore<EventEnvelope>>();
            await store.SaveAsync("actor-1", new EventEnvelope { Id = "evt-created-by-di" });
        }

        stateProxy.State.AgentStateTypeName.Should().Be(typeof(EventEnvelope).FullName);
        stateProxy.State.AgentStateSnapshot.Should().NotBeNull();
        stateProxy.WriteCount.Should().Be(1);
    }

    [Fact]
    public void IStateStoreResolution_WithoutBoundRuntimeState_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IStateStore<EventEnvelope>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Runtime actor state is not bound*");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleans_ShouldRegisterRuntimeActorStateStoreAsOpenGenericIStateStore()
    {
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IStateStore<>));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(RuntimeActorGrainStateStore<>));

        var accessorDescriptor = services.LastOrDefault(x => x.ServiceType == typeof(IRuntimeActorStateBindingAccessor));
        accessorDescriptor.Should().NotBeNull();
        accessorDescriptor!.ImplementationType.Should().Be(typeof(AsyncLocalRuntimeActorStateBindingAccessor));
    }

    private static CommittedStatePublicationState BuildPublicationCheckpoint(
        string actorId,
        long publishedVersion,
        string publishedEventId,
        long revision) =>
        new()
        {
            ActorId = actorId,
            Initialized = true,
            PublishedVersion = publishedVersion,
            PublishedEventId = publishedEventId,
            Revision = revision,
        };

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

            return GetDefault(targetMethod?.ReturnType);
        }

        private static object? GetDefault(Type? type)
        {
            if (type == null || type == typeof(void))
                return null;

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private class CommittedStatePublicationPersistentStateProxy : DispatchProxy
    {
        public RuntimeActorCommittedStatePublicationGrainState State { get; set; } = new();

        public int WriteCount { get; private set; }

        public int ReadCount { get; private set; }

        public int ClearCount { get; private set; }

        public bool CommitThenThrowOnNextWrite { get; set; }

        public bool FailBeforeCommitOnNextWrite { get; set; }

        public byte[]? PersistedCheckpoint { get; private set; }

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

            if (name == "WriteStateAsync")
            {
                WriteCount++;
                if (FailBeforeCommitOnNextWrite)
                {
                    FailBeforeCommitOnNextWrite = false;
                    throw new IOException("synthetic publication write failure before commit");
                }

                PersistedCheckpoint = State.Checkpoint?.ToArray();
                if (CommitThenThrowOnNextWrite)
                {
                    CommitThenThrowOnNextWrite = false;
                    throw new IOException("synthetic publication write acknowledgement loss");
                }

                return Task.CompletedTask;
            }

            if (name == "ClearStateAsync")
            {
                ClearCount++;
                return Task.CompletedTask;
            }

            if (name == "ReadStateAsync")
            {
                ReadCount++;
                State = new RuntimeActorCommittedStatePublicationGrainState
                {
                    Checkpoint = PersistedCheckpoint?.ToArray(),
                };
                return Task.CompletedTask;
            }

            if (name == "get_RecordExists")
                return true;

            if (name == "get_Etag")
                return string.Empty;

            if (name == "set_Etag")
                return null;

            return GetDefault(targetMethod?.ReturnType);
        }

        private static object? GetDefault(Type? type)
        {
            if (type == null || type == typeof(void))
                return null;

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
