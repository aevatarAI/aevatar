using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansDistributedCoverageTests
{
    [Fact]
    public void CompatibilityFailureInjectionPolicy_ShouldEnableForOldNodeWithConfiguredEventTypeUrls()
    {
        var policy = CompatibilityFailureInjectionPolicy.FromValues(
            "old",
            "type.googleapis.com/aevatar.events.NewEvent,type.googleapis.com/aevatar.events.OtherEvent");

        policy.Enabled.Should().BeTrue();
        policy.ShouldInject("type.googleapis.com/aevatar.events.NewEvent").Should().BeTrue();
        policy.ShouldInject("type.googleapis.com/aevatar.events.Unknown").Should().BeFalse();
    }

    [Fact]
    public void CompatibilityFailureInjectionPolicy_ShouldBeDisabledForNewNodeOrMissingConfig()
    {
        var policyForNewNode = CompatibilityFailureInjectionPolicy.FromValues(
            "new",
            "type.googleapis.com/aevatar.events.NewEvent");
        policyForNewNode.Enabled.Should().BeFalse();
        policyForNewNode.ShouldInject("type.googleapis.com/aevatar.events.NewEvent").Should().BeFalse();

        var policyWithoutTypeUrls = CompatibilityFailureInjectionPolicy.FromValues("old", "  ");
        policyWithoutTypeUrls.Enabled.Should().BeFalse();
        policyWithoutTypeUrls.ShouldInject("type.googleapis.com/aevatar.events.NewEvent").Should().BeFalse();
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldBuildRetryEnvelope_ForExplicitPolicy_WhenAttemptWithinLimit()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("2", "10");
        var envelope = CreateRetryPolicyEnvelope("evt-1");

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out var retryEnvelope,
            out var nextAttempt);

        built.Should().BeTrue();
        nextAttempt.Should().Be(1);
        retryEnvelope.Id.Should().Be(envelope.Id);
        retryEnvelope.Runtime!.Retry!.Attempt.Should().Be(1);
        retryEnvelope.Runtime.Retry.LastErrorType.Should().Be("InvalidOperationException");
        retryEnvelope.Runtime.Retry.OriginEventId.Should().Be("evt-1");
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldKeepRootOriginEventIdAcrossRetries()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("2", "10");
        var envelope = CreateRetryPolicyEnvelope("evt-retry-2");
        envelope.Runtime = new EnvelopeRuntime
        {
            Retry = new EnvelopeRetryContext
            {
                OriginEventId = "evt-root",
                Attempt = 1,
            },
        };

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out var retryEnvelope,
            out var nextAttempt);

        built.Should().BeTrue();
        nextAttempt.Should().Be(2);
        retryEnvelope.Id.Should().Be("evt-retry-2");
        retryEnvelope.Runtime!.Retry!.OriginEventId.Should().Be("evt-root");
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldRetryOccByDefault_WhenEnvironmentNotConfigured()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues(null, null);
        var envelope = CreateRetryPolicyEnvelope("evt-default-occ");

        policy.Enabled.Should().BeTrue();
        policy.MaxAttempts.Should().Be(3);
        policy.RetryDelayMs.Should().Be(1000);
        policy.RetryOnlyRecoverableConcurrencyFailures.Should().BeTrue();

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new EventStoreOptimisticConcurrencyException("actor-1", expectedVersion: 4, actualVersion: 5),
            out var retryEnvelope,
            out var nextAttempt);

        built.Should().BeTrue();
        nextAttempt.Should().Be(1);
        retryEnvelope.Runtime!.Retry!.Attempt.Should().Be(1);
        retryEnvelope.Runtime.Retry.LastErrorType.Should().Be(nameof(EventStoreOptimisticConcurrencyException));
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldRetryWrappedOccByDefault_WhenEnvironmentNotConfigured()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues(null, null);
        var envelope = CreateRetryPolicyEnvelope("evt-default-wrapped-occ");

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException(
                "wrapped",
                new EventStoreOptimisticConcurrencyException("actor-1", expectedVersion: 4, actualVersion: 5)),
            out var retryEnvelope,
            out var nextAttempt);

        built.Should().BeTrue();
        nextAttempt.Should().Be(1);
        retryEnvelope.Runtime!.Retry!.Attempt.Should().Be(1);
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldNotRetryNonOccByDefault_WhenEnvironmentNotConfigured()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues(null, null);
        var envelope = CreateRetryPolicyEnvelope("evt-default-non-occ");

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out _,
            out var nextAttempt);

        policy.Enabled.Should().BeTrue();
        policy.RetryOnlyRecoverableConcurrencyFailures.Should().BeTrue();
        built.Should().BeFalse();
        nextAttempt.Should().Be(1);
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldRetryNonOcc_WhenAttemptsExplicitlyConfigured()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("2", null);
        var envelope = CreateRetryPolicyEnvelope("evt-explicit-non-occ");

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out var retryEnvelope,
            out var nextAttempt);

        policy.RetryOnlyRecoverableConcurrencyFailures.Should().BeFalse();
        built.Should().BeTrue();
        nextAttempt.Should().Be(1);
        retryEnvelope.Runtime!.Retry!.Attempt.Should().Be(1);
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldKeepDefaultClassifier_WhenAttemptsConfigurationIsInvalid()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("not-a-number", null);
        var envelope = CreateRetryPolicyEnvelope("evt-invalid-config-non-occ");

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out _,
            out var nextAttempt);

        policy.Enabled.Should().BeTrue();
        policy.MaxAttempts.Should().Be(3);
        policy.RetryOnlyRecoverableConcurrencyFailures.Should().BeTrue();
        built.Should().BeFalse();
        nextAttempt.Should().Be(1);
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldUseSafeDelayDefault_WhenAttemptsConfiguredWithoutDelay()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("2", null);

        policy.Enabled.Should().BeTrue();
        policy.MaxAttempts.Should().Be(2);
        policy.RetryDelayMs.Should().Be(1000);
        policy.RetryOnlyRecoverableConcurrencyFailures.Should().BeFalse();
    }

    [Fact]
    public void RuntimeEnvelopeRetryPolicy_ShouldStopRetry_WhenAttemptExceedsLimit()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("1", "0");
        var envelope = new EventEnvelope
        {
            Id = "evt-2",
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new Google.Protobuf.WellKnownTypes.StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
            Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext
                {
                    Attempt = 1,
                },
            },
        };

        var built = policy.TryBuildRetryEnvelope(
            envelope,
            new InvalidOperationException("boom"),
            out _,
            out var nextAttempt);

        built.Should().BeFalse();
        nextAttempt.Should().Be(2);
    }

    private static EventEnvelope CreateRetryPolicyEnvelope(string id) =>
        new()
        {
            Id = id,
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new Google.Protobuf.WellKnownTypes.StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };

    [Fact]
    public async Task OrleansActorKindProbe_ShouldResolveAndNormalizeKind()
    {
        var grain = new RuntimeActorGrainStub { AgentKind = " " };
        var grainFactory = CreateGrainFactory((grainType, actorId) =>
        {
            grainType.Should().Be(typeof(IRuntimeActorGrain));
            actorId.Should().Be("actor-1");
            return grain;
        });
        var probe = new OrleansActorKindProbe(grainFactory);

        var resolved = await probe.GetRuntimeAgentKindAsync("actor-1");
        resolved.Should().BeNull();

        grain.AgentKind = "tests.recording-agent";
        resolved = await probe.GetRuntimeAgentKindAsync("actor-1");
        resolved.Should().Be("tests.recording-agent");
    }

    [Fact]
    public async Task OrleansActorKindProbe_ShouldValidateInputs()
    {
        var grainFactory = CreateGrainFactory((_, _) => new RuntimeActorGrainStub());
        var probe = new OrleansActorKindProbe(grainFactory);

        await Assert.ThrowsAsync<ArgumentException>(() => probe.GetRuntimeAgentKindAsync(""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.GetRuntimeAgentKindAsync("actor-2", cts.Token));
    }

    [Fact]
    public void OrleansStreamProviderAdapter_AndLifecycleManager_ShouldCacheAndEvictStreams()
    {
        var clusterClient = DispatchProxy.Create<IClusterClient, ClusterClientProxy>();
        var clusterClientProxy = (ClusterClientProxy)(object)clusterClient;
        var streamProvider = DispatchProxy.Create<global::Orleans.Streams.IStreamProvider, OrleansStreamProviderProxy>();
        clusterClientProxy.ServiceProvider = new FixedStreamProviderServiceProvider(streamProvider);
        var adapter = new OrleansStreamProviderAdapter(clusterClient, new AevatarOrleansRuntimeOptions
        {
            StreamProviderName = "provider-a",
            ActorEventNamespace = "aevatar.events",
        });

        var first = adapter.GetStream("actor-3");
        var second = adapter.GetStream("actor-3");
        second.Should().BeSameAs(first);

        var lifecycle = new StreamProviderLifecycleManager(adapter);
        lifecycle.RemoveStream("actor-3");
        var third = adapter.GetStream("actor-3");
        third.Should().NotBeSameAs(first);

        var getAct = () => adapter.GetStream("");
        getAct.Should().Throw<ArgumentException>();
        var removeAct = () => lifecycle.RemoveStream("");
        removeAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task StreamTopologyGrain_ShouldHandleUpsertRemoveAndClear()
    {
        var state = DispatchProxy.Create<IPersistentState<StreamTopologyGrainState>, StreamTopologyPersistentStateProxy>();
        var stateProxy = (StreamTopologyPersistentStateProxy)(object)state;
        var grain = new StreamTopologyGrain(state);

        var binding = CreateBinding("source-1", "target-1", 1, "lease-1");
        await grain.UpsertAsync(binding);
        stateProxy.WriteCount.Should().Be(1);

        var replacedBinding = CreateBinding("source-1", "target-1", 2, "lease-2");
        await grain.UpsertAsync(replacedBinding);
        stateProxy.WriteCount.Should().Be(2);

        var listed = await grain.ListAsync();
        listed.Should().ContainSingle();
        listed[0].Version.Should().Be(2);
        listed[0].LeaseId.Should().Be("lease-2");

        await grain.RemoveAsync("missing-target");
        stateProxy.WriteCount.Should().Be(2);

        await grain.RemoveAsync("target-1");
        stateProxy.WriteCount.Should().Be(3);

        await grain.ClearAsync();
        stateProxy.WriteCount.Should().Be(3);

        await grain.UpsertAsync(CreateBinding("source-1", "target-2", 3, null));
        await grain.ClearAsync();
        stateProxy.WriteCount.Should().Be(5);
        (await grain.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task StreamTopologyGrain_UpsertSameBinding_ShouldSkipDuplicateWrite()
    {
        var state = DispatchProxy.Create<IPersistentState<StreamTopologyGrainState>, StreamTopologyPersistentStateProxy>();
        var stateProxy = (StreamTopologyPersistentStateProxy)(object)state;
        var grain = new StreamTopologyGrain(state);

        var binding = CreateBinding("source-1", "target-1", 1, "lease-1");
        await grain.UpsertAsync(binding);
        stateProxy.WriteCount.Should().Be(1);

        await grain.UpsertAsync(CreateBinding("source-1", "target-1", 1, "lease-1"));
        stateProxy.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task StreamTopologyGrain_ShouldSupportLegacyListState()
    {
        var state = DispatchProxy.Create<IPersistentState<StreamTopologyGrainState>, StreamTopologyPersistentStateProxy>();
        var stateProxy = (StreamTopologyPersistentStateProxy)(object)state;
        stateProxy.State.Bindings.Add(new StreamForwardingBindingEntry
        {
            SourceStreamId = "source-legacy",
            TargetStreamId = "target-legacy",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [TopologyAudience.Children],
            EventTypeFilter = ["evt"],
            Version = 7,
            LeaseId = "lease-legacy",
        });

        var grain = new StreamTopologyGrain(state);
        var listed = await grain.ListAsync();

        listed.Should().ContainSingle();
        listed[0].SourceStreamId.Should().Be("source-legacy");
        listed[0].TargetStreamId.Should().Be("target-legacy");
        listed[0].Version.Should().Be(7);
        listed[0].LeaseId.Should().Be("lease-legacy");
        stateProxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task StreamTopologyGrain_UpsertWithSameCountsButDifferentDirectionOrEventType_ShouldWrite()
    {
        var state = DispatchProxy.Create<IPersistentState<StreamTopologyGrainState>, StreamTopologyPersistentStateProxy>();
        var stateProxy = (StreamTopologyPersistentStateProxy)(object)state;
        var grain = new StreamTopologyGrain(state);

        await grain.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "source-1",
            TargetStreamId = "target-1",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = new HashSet<TopologyAudience> { TopologyAudience.Children, TopologyAudience.ParentAndChildren },
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt-a", "evt-b" },
            Version = 1,
            LeaseId = "lease-1",
        });
        stateProxy.WriteCount.Should().Be(1);

        await grain.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "source-1",
            TargetStreamId = "target-1",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = new HashSet<TopologyAudience> { TopologyAudience.Children, TopologyAudience.Parent },
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt-a", "evt-b" },
            Version = 1,
            LeaseId = "lease-1",
        });
        stateProxy.WriteCount.Should().Be(2);

        await grain.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "source-1",
            TargetStreamId = "target-1",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = new HashSet<TopologyAudience> { TopologyAudience.Children, TopologyAudience.Parent },
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt-a", "evt-c" },
            Version = 1,
            LeaseId = "lease-1",
        });
        stateProxy.WriteCount.Should().Be(3);

        var listed = await grain.ListAsync();
        listed.Should().ContainSingle();
        listed[0].DirectionFilter.Should().BeEquivalentTo(new[] { TopologyAudience.Children, TopologyAudience.Parent });
        listed[0].EventTypeFilter.Should().BeEquivalentTo("evt-a", "evt-c");
    }

    [Fact]
    public async Task StreamTopologyGrain_LegacyBindingsWithBlankTarget_ShouldSkipInvalidEntry()
    {
        var state = DispatchProxy.Create<IPersistentState<StreamTopologyGrainState>, StreamTopologyPersistentStateProxy>();
        var stateProxy = (StreamTopologyPersistentStateProxy)(object)state;
        stateProxy.State.Bindings.Add(new StreamForwardingBindingEntry
        {
            SourceStreamId = "source-legacy",
            TargetStreamId = " ",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [TopologyAudience.Children],
            EventTypeFilter = ["evt"],
            Version = 1,
            LeaseId = "lease-invalid",
        });
        stateProxy.State.Bindings.Add(new StreamForwardingBindingEntry
        {
            SourceStreamId = "source-legacy",
            TargetStreamId = "target-valid",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [TopologyAudience.Children],
            EventTypeFilter = ["evt"],
            Version = 2,
            LeaseId = "lease-valid",
        });

        var grain = new StreamTopologyGrain(state);
        var listed = await grain.ListAsync();

        listed.Should().ContainSingle();
        listed[0].TargetStreamId.Should().Be("target-valid");
        listed[0].LeaseId.Should().Be("lease-valid");
        stateProxy.State.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimeActorGrain_ShouldManageStateWithoutActivationContext()
    {
        var state = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var stateProxy = (RuntimeActorPersistentStateProxy)(object)state;
        var grain = new RuntimeActorGrain(state);

        (await grain.IsInitializedAsync()).Should().BeFalse();
        stateProxy.State.Identity = new RuntimeActorIdentity { Kind = "tests.known-kind" };
        (await grain.IsInitializedAsync()).Should().BeTrue();
        (await grain.GetAgentKindAsync()).Should().Be("tests.known-kind");

        await grain.AddChildAsync("child-1");
        await grain.AddChildAsync("child-1");
        await grain.AddChildAsync("child-2");
        (await grain.GetChildrenAsync()).Should().BeEquivalentTo("child-1", "child-2");

        await grain.RemoveChildAsync("missing-child");
        await grain.RemoveChildAsync("child-2");
        (await grain.GetChildrenAsync()).Should().ContainSingle("child-1");

        await grain.SetParentAsync("parent-1");
        (await grain.GetParentAsync()).Should().Be("parent-1");
        await grain.ClearParentAsync();
        (await grain.GetParentAsync()).Should().BeNull();
        await grain.ClearParentAsync();

        stateProxy.State.AgentId = "actor-1";
        stateProxy.State.AgentStateTypeName = typeof(EventEnvelope).FullName;
        stateProxy.State.AgentStateSnapshot = new EventEnvelope { Id = "snapshot" }.ToByteArray();
        await grain.PurgeAsync();
        stateProxy.State.AgentId.Should().BeEmpty();
        stateProxy.State.Identity.Should().BeNull();
        stateProxy.State.ParentId.Should().BeNull();
        stateProxy.State.Children.Should().BeEmpty();
        stateProxy.State.AgentStateTypeName.Should().BeNull();
        stateProxy.State.AgentStateSnapshot.Should().BeNull();
        stateProxy.ClearCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RuntimeActorGrain_ShouldCoverExceptionalBranches()
    {
        var state = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var grain = new RuntimeActorGrain(state);

        var initialized = await grain.InitializeAgentByKindAsync("tests.never-registered");
        initialized.Should().BeFalse();
    }

    private static StreamForwardingBinding CreateBinding(
        string source,
        string target,
        long version,
        string? leaseId) =>
        new()
        {
            SourceStreamId = source,
            TargetStreamId = target,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = new HashSet<TopologyAudience> { TopologyAudience.Children, TopologyAudience.ParentAndChildren },
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt" },
            Version = version,
            LeaseId = leaseId,
        };

    private static IGrainFactory CreateGrainFactory(Func<Type, string, object> resolver)
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var proxy = (GrainFactoryProxy)(object)grainFactory;
        proxy.Resolver = resolver;
        return grainFactory;
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<Type, string, object>? Resolver { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                args is { Length: > 0 } &&
                args[0] is string id &&
                Resolver != null)
            {
                var grainType = targetMethod.GetGenericArguments()[0];
                return Resolver(grainType, id);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private class ClusterClientProxy : DispatchProxy
    {
        public IServiceProvider? ServiceProvider { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (targetMethod?.Name == "get_ServiceProvider")
                return ServiceProvider ?? throw new InvalidOperationException("Missing service provider.");

            throw new NotSupportedException($"Unexpected cluster client call: {targetMethod?.Name}");
        }
    }

    private class OrleansStreamProviderProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = targetMethod;
            _ = args;
            return null;
        }
    }

    private sealed class RuntimeActorGrainStub : IRuntimeActorGrain
    {
        public string AgentKind { get; set; } = string.Empty;

        public Task<bool> InitializeAgentByKindAsync(string kind)
        {
            AgentKind = kind;
            return Task.FromResult(true);
        }

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = envelopeBytes;
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId)
        {
            _ = childId;
            return Task.CompletedTask;
        }

        public Task RemoveChildAsync(string childId)
        {
            _ = childId;
            return Task.CompletedTask;
        }

        public Task SetParentAsync(string parentId)
        {
            _ = parentId;
            return Task.CompletedTask;
        }

        public Task ClearParentAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetChildrenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetParentAsync() => Task.FromResult<string?>(null);

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<string> GetAgentKindAsync() => Task.FromResult(AgentKind);

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task PurgeAsync() => Task.CompletedTask;
    }

    private class StreamTopologyPersistentStateProxy : DispatchProxy
    {
        public StreamTopologyGrainState State { get; set; } = new();

        public int WriteCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name;
            if (name == "get_State")
                return State;
            if (name == "set_State")
            {
                State = args?[0] as StreamTopologyGrainState ?? new StreamTopologyGrainState();
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
    }

    private class RuntimeActorPersistentStateProxy : DispatchProxy
    {
        public RuntimeActorGrainState State { get; set; } = new();

        public int WriteCount { get; private set; }

        public int ClearCount { get; private set; }

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
            if (name == "ClearStateAsync")
            {
                ClearCount++;
                return Task.CompletedTask;
            }
            if (name == "ReadStateAsync")
                return Task.CompletedTask;
            if (name == "get_RecordExists")
                return true;
            if (name == "get_Etag")
                return string.Empty;
            if (name == "set_Etag")
                return null;

            return GetDefault(targetMethod?.ReturnType);
        }
    }

    private static object? GetDefault(Type? type)
    {
        if (type == null || type == typeof(void))
            return null;

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private sealed class FixedStreamProviderServiceProvider(
        global::Orleans.Streams.IStreamProvider streamProvider) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(global::Orleans.Streams.IStreamProvider))
                return streamProvider;

            if (serviceType == typeof(IEnumerable<global::Orleans.Streams.IStreamProvider>))
                return new[] { streamProvider };

            if (serviceType == typeof(IKeyedServiceProvider))
                return this;

            return null;
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            _ = serviceKey;
            return serviceType == typeof(global::Orleans.Streams.IStreamProvider)
                ? streamProvider
                : null;
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            return GetKeyedService(serviceType, serviceKey)
                   ?? throw new InvalidOperationException($"Service not found: {serviceType.FullName}");
        }
    }
}
