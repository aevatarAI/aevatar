using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behavior tests for <see cref="ExternalIdentityBindingGAgent"/>: state
/// transitions, idempotent commit under concurrent /init, and revoke-driven
/// projection repair when no binding exists. Pinned by ADR-0017 §Implementation
/// Notes #2.
/// </summary>
public class ExternalIdentityBindingGAgentTests : IAsyncLifetime
{
    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    private ExternalIdentityBindingGAgent _agent = null!;
    private RecordingBindingRetirementPort _retirementPort = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        // HandleEventAsync resolves a runtime callback scheduler for self-
        // continuation timers; tests register a no-op so the dispatch path
        // is exercised without bringing up a real Orleans cluster.
        services.AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler, NoopCallbackScheduler>();
        _retirementPort = new RecordingBindingRetirementPort();
        services.AddSingleton<INyxIdBindingRetirementPort>(_retirementPort);

        _serviceProvider = services.BuildServiceProvider();

        _agent = new ExternalIdentityBindingGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(),
        };

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    private static ExternalSubjectRef NyxIdSubject() => new()
    {
        Platform = OwnerScope.NyxIdPlatform,
        Tenant = string.Empty,
        ExternalUserId = "nyx-user-1",
    };

    [Fact]
    public async Task HandleCommitBinding_PersistsBoundState()
    {
        var subject = SampleSubject();

        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
        _agent.State.BoundAt.Should().NotBeNull();
        _agent.State.RevokedAt.Should().BeNull();
        _agent.State.ExternalSubject.Should().NotBeNull();
        _agent.State.ExternalSubject!.Platform.Should().Be("lark");
    }

    [Fact]
    public async Task HandleCommitBinding_IsIdempotentUnderConcurrentInit()
    {
        var subject = SampleSubject();

        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        var afterFirstVersion = _agent.EventSourcing!.CurrentVersion;

        // Second concurrent /init lands after the first one already
        // committed. The actor MUST keep the existing binding_id and discard
        // the second one (ADR-0018 §Implementation Notes #2) without
        // persisting projection-only events.
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
            OwnerScopeId = "owner-user-1",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
        _agent.EventSourcing!.CurrentVersion.Should().Be(
            afterFirstVersion,
            "the discard branch must not append a projection-only no-op event");
    }

    [Fact]
    public async Task HandleCommitBinding_RejectsEmptyBindingId()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = SampleSubject(),
            BindingId = string.Empty,
        });

        _agent.State.BindingId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCommitBinding_RejectsNyxIdSubjectWithoutOwnerScope()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = NyxIdSubject(),
            BindingId = "bnd_nyxid",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.OwnerScopeId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCommitBinding_IgnoresNullExternalSubject()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = null,
            BindingId = "bnd_x",
            OwnerScopeId = "owner-user-1",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.ExternalSubject.Should().BeNull();
    }

    [Fact]
    public async Task HandleReplaceBinding_ReplacesExpectedBindingThenRetiresIt()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });

        await _agent.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
            OwnerScopeId = "owner-user-1",
            ExpectedPreviousBindingId = "bnd_first",
            Reason = "studio_service_access_review",
        });

        _agent.State.BindingId.Should().Be("bnd_second");
        _agent.State.BoundAt.Should().NotBeNull();
        _agent.State.RevokedAt.Should().BeNull();
        _agent.State.PendingRetirementBindingIds.Should().BeEmpty();
        _retirementPort.RetiredBindingIds.Should().Equal("bnd_first");
    }

    [Fact]
    public async Task HandleReplaceBinding_IsNoOpWhenBindingIdIsAlreadyCurrent()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        var afterFirstVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
            ExpectedPreviousBindingId = "bnd_first",
            Reason = "studio_service_access_review",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
        _agent.EventSourcing!.CurrentVersion.Should().Be(afterFirstVersion);
        _retirementPort.RetiredBindingIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReplaceBinding_RejectsNyxIdSubjectWithoutOwnerScope()
    {
        var subject = NyxIdSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        var afterFirstVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
            ExpectedPreviousBindingId = "bnd_first",
            Reason = "studio_service_access_review",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
        _agent.State.OwnerScopeId.Should().Be("owner-user-1");
        _agent.EventSourcing!.CurrentVersion.Should().Be(afterFirstVersion);
        _retirementPort.RetiredBindingIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReplaceBinding_RejectsStaleExpectedBindingAndRetiresIncomingBinding()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_current",
            OwnerScopeId = "owner-user-1",
        });

        await _agent.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_unadopted",
            OwnerScopeId = "owner-user-1",
            ExpectedPreviousBindingId = "bnd_stale",
            Reason = "studio_service_access_review",
        });

        _agent.State.BindingId.Should().Be("bnd_current");
        _agent.State.PendingRetirementBindingIds.Should().BeEmpty();
        _retirementPort.RetiredBindingIds.Should().Equal("bnd_unadopted");
    }

    [Fact]
    public async Task HandleReplaceBinding_PersistsFailedRetirementAndRetriesOnActivation()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        _retirementPort.Failure = new HttpRequestException("NyxID unavailable");

        await _agent.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
            OwnerScopeId = "owner-user-1",
            ExpectedPreviousBindingId = "bnd_first",
            Reason = "studio_service_access_review",
        });

        _agent.State.BindingId.Should().Be("bnd_second");
        _agent.State.PendingRetirementBindingIds.Should().Equal("bnd_first");

        _retirementPort.Failure = null;
        var reactivated = new ExternalIdentityBindingGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(),
        };

        await reactivated.ActivateAsync();

        reactivated.State.BindingId.Should().Be("bnd_second");
        reactivated.State.PendingRetirementBindingIds.Should().BeEmpty();
        _retirementPort.RetiredBindingIds.Should().Contain("bnd_first");
    }

    [Fact]
    public async Task HandleRevokeBinding_IgnoresNullExternalSubject()
    {
        // Seed an existing binding first so we can verify revoke is a no-op.
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = SampleSubject(),
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });

        await _agent.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = null,
            Reason = "stray",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
        _agent.State.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task HandleRevokeBinding_ClearsBindingId()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });

        await _agent.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = subject,
            Reason = "user_unbind",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRevokeBinding_IsNoOpWhenNoActiveBinding()
    {
        var initialVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = SampleSubject(),
            Reason = "stray_unbind",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.RevokedAt.Should().BeNull();
        _agent.EventSourcing!.CurrentVersion.Should().Be(
            initialVersion,
            "empty revoke must not append a projection-only no-op event");
    }

    [Fact]
    public async Task HandleEventAsync_DispatchesCommitBindingThroughEnvelope()
    {
        // Earlier rounds (mimo-v2.5-pro L37 / codex L50) flagged that the
        // test suite did not exercise the envelope -> [EventHandler] dispatch
        // path, only the handler bodies. This test packs a
        // CommitBindingCommand into an EventEnvelope, drives it through
        // HandleEventAsync, and asserts the resulting state mutation. If
        // the [EventHandler] reflection / handler.CanHandle wiring drifts,
        // this assertion fires.
        var subject = SampleSubject();
        var envelope = new EventEnvelope
        {
            Id = "envelope-1",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new CommitBindingCommand
            {
                ExternalSubject = subject,
                BindingId = "bnd_dispatched",
                OwnerScopeId = "owner-user-1",
            }),
        };

        await _agent.HandleEventAsync(envelope, default);

        _agent.State.BindingId.Should().Be("bnd_dispatched");
        _agent.State.BoundAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RebindAfterRevoke_AcceptsNewBindingId()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        await _agent.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = subject,
            Reason = "user_unbind",
        });

        // After /unbind, a fresh /init MUST be able to bind a new binding_id.
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
            OwnerScopeId = "owner-user-1",
        });

        _agent.State.BindingId.Should().Be("bnd_second");
        _agent.State.RevokedAt.Should().BeNull();
    }

    // ─── Test doubles ───

    private sealed class NoopCallbackScheduler : Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler
    {
        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleTimeoutAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 0,
                Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleTimerAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 0,
                Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingBindingRetirementPort : INyxIdBindingRetirementPort
    {
        public Exception? Failure { get; set; }
        public List<string> RetiredBindingIds { get; } = [];

        public Task RetireAsync(string bindingId, CancellationToken ct = default)
        {
            if (Failure is not null)
                return Task.FromException(Failure);

            RetiredBindingIds.Add(bindingId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
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

    // Refactor (iter97/cluster-097): Old pattern: tests injected a hidden
    // committed-state activation service and expected identity no-op commands
    // to side-dispatch projection envelopes. New principle: no-op commands
    // only preserve actor facts; committed-state hook/plan provider own
    // materialization, and repair belongs to explicit maintenance/admin.
}
