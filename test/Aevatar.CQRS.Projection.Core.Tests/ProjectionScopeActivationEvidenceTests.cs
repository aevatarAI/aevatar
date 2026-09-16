using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.CQRS.Projection.Core.Observability;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeActivationEvidenceTests
{
    private const string RootActorId = "root-fast-path";
    private const string ProjectionKind = "projection-fast-path";
    private const string ScopeAgentKind = "projection.materialization-scope.evidence-context";

    [Fact]
    public async Task EnsureAsync_ExactAuthoritativeEvidence_ShouldAvoidRuntimeKindAndDispatchCalls()
    {
        var fixture = CreateFixture();
        fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
        var verifierGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Verifier.Handler = (_, _, ct) => verifierGate.Task.WaitAsync(ct);

        var lease = await fixture.Service.EnsureAsync(Request());

        lease.Should().NotBeNull();
        fixture.Runtime.ExistsCallCount.Should().Be(0);
        fixture.Runtime.CreateCallCount.Should().Be(0);
        fixture.Verifier.CallCount.Should().Be(0);
        fixture.Dispatch.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAsync_LegacyWireEvidence_ShouldRequireSynchronousColdRepairOnEveryAttempt()
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
        fixture.Dispatch.Handler = (_, _) =>
        {
            ProjectionScopeObservationRelayBinding.IsLegacyReadinessProbe(
                    fixture.Authority.Binding,
                    RootActorId,
                    fixture.ScopeActorId)
                .Should().BeTrue();
            fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
            return Task.CompletedTask;
        };

        await fixture.Service.EnsureAsync(Request());
        await fixture.Service.EnsureAsync(Request());

        fixture.Runtime.ExistsCallCount.Should().Be(2);
        fixture.Verifier.CallCount.Should().Be(2);
        fixture.Dispatch.CallCount.Should().Be(2);
        fixture.Authority.UpsertCallCount.Should().Be(2);
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId,
                ScopeAgentKind)
            .Should().BeFalse();
        ProjectionScopeObservationRelayBinding.IsLegacyCompatibleActivationEvidence(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId)
            .Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_PreexistingLegacyEvidence_ShouldNotTreatDispatchAdmissionAsRepair()
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatch.Handler = (_, _) =>
        {
            dispatched.TrySetResult();
            return Task.CompletedTask;
        };

        var activation = fixture.Service.EnsureAsync(Request());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        activation.IsCompleted.Should().BeFalse();
        ProjectionScopeObservationRelayBinding.IsLegacyReadinessProbe(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId)
            .Should().BeTrue();

        fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
        await activation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnsureAsync_LegacyEvidenceWrittenDuringActorVerification_ShouldBeChallengedBeforeDispatch()
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Verifier.Handler = (_, _, _) =>
        {
            fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
            return Task.FromResult(true);
        };
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatch.Handler = (_, _) =>
        {
            dispatched.TrySetResult();
            return Task.CompletedTask;
        };

        var activation = fixture.Service.EnsureAsync(Request());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        activation.IsCompleted.Should().BeFalse();
        ProjectionScopeObservationRelayBinding.IsLegacyReadinessProbe(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId)
            .Should().BeTrue();

        fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
        await activation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnsureAsync_ExistingActorWithoutKindVerifier_ShouldRejectLegacyEvidence()
    {
        var fixture = CreateFixture(includeVerifier: false);
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatch.Handler = (_, _) =>
        {
            dispatched.TrySetResult();
            return Task.CompletedTask;
        };

        var activation = fixture.Service.EnsureAsync(Request());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        activation.IsCompleted.Should().BeFalse();
        fixture.Verifier.CallCount.Should().Be(0);
        fixture.Authority.UpsertCallCount.Should().Be(0);

        fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
        await activation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnsureAsync_CreatedActorWithoutKindVerifier_ShouldAcceptNewLegacyEvidence()
    {
        var fixture = CreateFixture(includeVerifier: false);
        fixture.Dispatch.Handler = (_, _) =>
        {
            fixture.Authority.Binding = LegacyBinding(fixture.ScopeActorId);
            return Task.CompletedTask;
        };

        await fixture.Service.EnsureAsync(Request());

        fixture.Runtime.CreateCallCount.Should().Be(1);
        fixture.Verifier.CallCount.Should().Be(0);
        fixture.Authority.UpsertCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("mode")]
    [InlineData("direction")]
    [InlineData("filter")]
    [InlineData("kind")]
    [InlineData("generation")]
    public async Task EnsureAsync_MismatchedEvidence_ShouldRunSynchronousColdRepair(string mismatch)
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = MismatchedBinding(fixture.ScopeActorId, mismatch);
        fixture.Dispatch.Handler = (_, _) =>
        {
            fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
            return Task.CompletedTask;
        };

        await fixture.Service.EnsureAsync(Request());

        fixture.Runtime.ExistsCallCount.Should().Be(1);
        fixture.Verifier.CallCount.Should().Be(1);
        fixture.Dispatch.CallCount.Should().Be(1);
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId,
                ScopeAgentKind)
            .Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_ColdPath_ShouldNotReturnBeforeExactRelayIsAuthoritativelyVisible()
    {
        var fixture = CreateFixture();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dispatch.Handler = (_, _) =>
        {
            dispatched.TrySetResult();
            return Task.CompletedTask;
        };

        var activation = fixture.Service.EnsureAsync(Request());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        activation.IsCompleted.Should().BeFalse();

        fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
        await activation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnsureAsync_ConcurrentColdCalls_ShouldConvergeOnOneActorCreationAndExactEvidence()
    {
        var fixture = CreateFixture();
        var bothCallersInColdPath = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorityReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Both cold callers are held inside their own first authority read, so neither can leave the
        // cold path before the other has provably entered it. No wall clock decides that overlap.
        fixture.Authority.Handler = async (count, ct) =>
        {
            if (count <= 2)
            {
                if (count == 2)
                    bothCallersInColdPath.TrySetResult();
                await releaseAuthorityReads.Task.WaitAsync(ct);
                return null;
            }

            return fixture.Authority.Binding;
        };
        // The runtime gate opens for the caller that reaches it first and holds the other one there
        // until that caller's actor creation has completed. Convergence on a single creation is then
        // a claim about the activation service, not about which thread the scheduler happened to run.
        fixture.Runtime.ExistsGate = callOrdinal =>
            callOrdinal == 1 ? Task.CompletedTask : fixture.Runtime.FirstCreateCompleted;
        fixture.Dispatch.Handler = (_, _) =>
        {
            fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
            return Task.CompletedTask;
        };

        var first = fixture.Service.EnsureAsync(Request());
        var second = fixture.Service.EnsureAsync(Request());
        await bothCallersInColdPath.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseAuthorityReads.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Runtime.ExistsCallCount.Should().Be(2);
        fixture.Runtime.CreateCallCount.Should().Be(1);
        fixture.Dispatch.CallCount.Should().Be(2);
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                fixture.Authority.Binding,
                RootActorId,
                fixture.ScopeActorId,
                ScopeAgentKind)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseIfIdleAsync_ShouldWaitUntilAuthoritativeRelayIsAbsent()
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Authority.OnRead = count =>
        {
            if (count >= 2)
                releaseRead.TrySetResult();
        };
        var release = new ProjectionScopeReleaseService<
            EvidenceLease,
            ProjectionMaterializationScopeGAgent<EvidenceContext>>(
            fixture.Runtime,
            fixture.Dispatch,
            _ => ScopeKey(),
            fixture.Verifier,
            CreateKindRegistry(),
            fixture.Authority,
            fixture.Authority);

        var task = release.ReleaseIfIdleAsync(new EvidenceLease(new EvidenceContext
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
        }));
        await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        task.IsCompleted.Should().BeFalse();

        fixture.Authority.Binding = null;
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReleaseIfIdleAsync_AfterEvidenceDisappears_ShouldForceNextActivationThroughColdPath()
    {
        var fixture = CreateFixture();
        fixture.Runtime.Exists = true;
        fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
        fixture.Dispatch.Handler = (_, envelope) =>
        {
            if (envelope.Payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
                fixture.Authority.Binding = null;
            else if (envelope.Payload?.Is(EnsureProjectionScopeCommand.Descriptor) == true)
                fixture.Authority.Binding = ExactBinding(fixture.ScopeActorId);
            return Task.CompletedTask;
        };
        var release = new ProjectionScopeReleaseService<
            EvidenceLease,
            ProjectionMaterializationScopeGAgent<EvidenceContext>>(
            fixture.Runtime,
            fixture.Dispatch,
            _ => ScopeKey(),
            fixture.Verifier,
            CreateKindRegistry(),
            fixture.Authority,
            fixture.Authority);
        var lease = new EvidenceLease(new EvidenceContext
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
        });

        await release.ReleaseIfIdleAsync(lease);
        var runtimeCallsAfterRelease = fixture.Runtime.ExistsCallCount;
        var dispatchCallsAfterRelease = fixture.Dispatch.CallCount;
        await fixture.Service.EnsureAsync(Request());

        fixture.Runtime.ExistsCallCount.Should().Be(runtimeCallsAfterRelease + 1);
        fixture.Verifier.CallCount.Should().Be(1);
        fixture.Dispatch.CallCount.Should().Be(dispatchCallsAfterRelease + 1);
    }

    [Fact]
    public void ProjectionActivationMetrics_ShouldEmitLowCardinalityStageAndPathLabels()
    {
        var measurements = new ConcurrentBag<(string Instrument, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ProjectionProcessingMetrics.MeterName &&
                    instrument.Name.StartsWith("aevatar.projection.activation.", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(item => item.Key, item => item.Value))));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(item => item.Key, item => item.Value))));
        listener.Start();

        ProjectionActivationMetrics.RecordStage(
            ProjectionActivationMetrics.AuthorityLookupStage,
            ProjectionActivationMetrics.StartTimestamp(),
            ProjectionRuntimeMode.DurableMaterialization,
            "hit");
        ProjectionActivationMetrics.RecordResult(
            "warm",
            ProjectionRuntimeMode.DurableMaterialization,
            "success");

        measurements.Should().Contain(item =>
            item.Instrument == "aevatar.projection.activation.stage.duration" &&
            Equals(item.Tags["stage"], "authority_lookup") &&
            Equals(item.Tags["outcome"], "hit") &&
            Equals(item.Tags["mode"], "durable"));
        measurements.Should().Contain(item =>
            item.Instrument == "aevatar.projection.activation.result.total" &&
            Equals(item.Tags["path"], "warm") &&
            Equals(item.Tags["outcome"], "success") &&
            Equals(item.Tags["mode"], "durable"));
    }

    private static Fixture CreateFixture(bool includeVerifier = true)
    {
        var runtime = new CountingRuntime();
        var verifier = new CountingKindVerifier();
        var authority = new RecordingAuthority();
        var dispatch = new CountingDispatchPort();
        var service = new ProjectionScopeActivationService<
            EvidenceLease,
            EvidenceContext,
            ProjectionMaterializationScopeGAgent<EvidenceContext>>(
            runtime,
            dispatch,
            request => new EvidenceContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            },
            (_, context) => new EvidenceLease(context),
            includeVerifier ? verifier : null,
            CreateKindRegistry(),
            bindingAuthority: authority);
        return new Fixture(runtime, verifier, authority, dispatch, service, ProjectionScopeActorId.Build(ScopeKey()));
    }

    private static IAgentKindRegistry CreateKindRegistry() => new AgentKindRegistry(
    [
        ProjectionScopeAgentRegistration.Create<ProjectionMaterializationScopeGAgent<EvidenceContext>>(),
    ]);

    private static ProjectionScopeStartRequest Request() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = ProjectionKind,
        Mode = ProjectionRuntimeMode.DurableMaterialization,
    };

    private static ProjectionRuntimeScopeKey ScopeKey() =>
        new(RootActorId, ProjectionKind, ProjectionRuntimeMode.DurableMaterialization);

    private static StreamForwardingBinding ExactBinding(string targetActorId) =>
        ProjectionScopeObservationRelayBinding.Create(RootActorId, targetActorId, ScopeAgentKind, 7);

    private static StreamForwardingBinding LegacyBinding(string targetActorId)
    {
        var binding = ExactBinding(targetActorId);
        binding.TargetActorKind = string.Empty;
        binding.ActivationGeneration = 0;
        return binding;
    }

    private static StreamForwardingBinding MismatchedBinding(string targetActorId, string mismatch)
    {
        var binding = ExactBinding(targetActorId);
        switch (mismatch)
        {
            case "source":
                binding.SourceStreamId = "wrong-source";
                break;
            case "target":
                binding.TargetStreamId = "wrong-target";
                break;
            case "mode":
                binding.ForwardingMode = StreamForwardingMode.TransitOnly;
                break;
            case "direction":
                binding.DirectionFilter.Add(TopologyAudience.Children);
                break;
            case "filter":
                binding.EventTypeFilter.Add("type.googleapis.com/example.OtherEvent");
                break;
            case "kind":
                binding.TargetActorKind = "projection.other-scope";
                break;
            case "generation":
                binding.ActivationGeneration = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch));
        }

        return binding;
    }

    private sealed record Fixture(
        CountingRuntime Runtime,
        CountingKindVerifier Verifier,
        RecordingAuthority Authority,
        CountingDispatchPort Dispatch,
        ProjectionScopeActivationService<
            EvidenceLease,
            EvidenceContext,
            ProjectionMaterializationScopeGAgent<EvidenceContext>> Service,
        string ScopeActorId);

    private sealed class EvidenceContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;
        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class EvidenceLease(EvidenceContext context)
        : ProjectionRuntimeLeaseBase(context.RootActorId),
          IProjectionContextRuntimeLease<EvidenceContext>
    {
        public EvidenceContext Context { get; } = context;
    }

    private sealed class CountingKindVerifier : IAgentKindVerifier
    {
        public int CallCount { get; private set; }
        public Func<string, string, CancellationToken, Task<bool>> Handler { get; set; } =
            static (_, _, _) => Task.FromResult(true);

        public Task<bool> IsExpectedKindAsync(string actorId, string expectedKind, CancellationToken ct = default)
        {
            CallCount++;
            return Handler(actorId, expectedKind, ct);
        }
    }

    private sealed class RecordingAuthority : IStreamForwardingBindingAuthority, IStreamForwardingRegistry
    {
        private int _readCount;
        public StreamForwardingBinding? Binding { get; set; }
        public int UpsertCallCount { get; private set; }
        public Action<int>? OnRead { get; set; }
        public Func<int, CancellationToken, Task<StreamForwardingBinding?>>? Handler { get; set; }

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _readCount);
            OnRead?.Invoke(count);
            return Handler?.Invoke(count, ct) ?? Task.FromResult(Binding);
        }

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCallCount++;
            Binding = binding;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            Binding = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(Binding == null ? [] : [Binding]);
    }

    private sealed class CountingDispatchPort : IActorDispatchPort
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Func<string, EventEnvelope, Task> Handler { get; set; } = static (_, _) => Task.CompletedTask;

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            await Handler(actorId, envelope);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class CountingRuntime : IActorRuntime
    {
        private readonly TaskCompletionSource _firstCreateCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _existsCallCount;
        private int _createCallCount;
        private volatile bool _exists;

        public bool Exists
        {
            get => _exists;
            set => _exists = value;
        }

        public int ExistsCallCount => Volatile.Read(ref _existsCallCount);
        public int CreateCallCount => Volatile.Read(ref _createCallCount);

        /// <summary>
        /// Awaited on entry to <see cref="ExistsAsync"/> with the one-based call ordinal. It lets a
        /// concurrency test hold a caller at the runtime boundary until something it chose has
        /// provably happened, instead of leaving the interleaving to the scheduler.
        /// </summary>
        public Func<int, Task> ExistsGate { get; set; } = static _ => Task.CompletedTask;

        /// <summary>Completes once the first <see cref="CreateByKindAsync"/> call has recorded its actor.</summary>
        public Task FirstCreateCompleted => _firstCreateCompleted.Task;

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _createCallCount);
            _exists = true;
            _firstCreateCompleted.TrySetResult();
            return Task.FromResult<IActor>(new TestActor(id!));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public async Task<bool> ExistsAsync(string id)
        {
            var callOrdinal = Interlocked.Increment(ref _existsCallCount);
            await ExistsGate(callOrdinal).ConfigureAwait(false);
            return _exists;
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
