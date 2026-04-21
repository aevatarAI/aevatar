using System.Reflection;
using Aevatar.GAgents.ChannelRuntime;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
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
                    TemplateName = "daily_report",
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
    public async Task IStateStoreResolution_WithBoundRuntimeState_ShouldUseRuntimeActorStateStore()
    {
        var runtimeState = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)runtimeState;
        var services = new ServiceCollection();
        services.AddAevatarFoundationRuntimeOrleans();
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IRuntimeActorStateBindingAccessor>();

        IStateStore<EventEnvelope> store;
        using (accessor.Bind(runtimeState))
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
}
