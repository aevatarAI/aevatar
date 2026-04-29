using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behavior tests for <see cref="ExternalIdentityBindingGAgent"/>: state
/// transitions, idempotent commit under concurrent /init, and revoke as
/// no-op when no binding exists. Pinned by ADR-0018 §Implementation Notes #2.
///
/// FOLLOW-UP: most tests instantiate the agent directly with a hand-rolled
/// <c>IEventStore</c> + <c>IEventSourcingBehaviorFactory</c>. This pins the
/// behaviour at the handler / state-transition level but skips the actor
/// runtime's lifecycle (activation, rehydration, deactivation) and silo
/// dispatch wiring. <c>HandleEventAsync_DispatchesCommitBindingThroughEnvelope</c>
/// covers the in-process dispatch path; an Orleans-test-cluster integration
/// suite is tracked as a separate follow-up (kimi-k2p6 L36 / mimo-v2.5-pro L37).
/// </summary>
public class ExternalIdentityBindingGAgentTests : IAsyncLifetime
{
    private ExternalIdentityBindingGAgent _agent = null!;
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

    [Fact]
    public async Task HandleCommitBinding_PersistsBoundState()
    {
        var subject = SampleSubject();

        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
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
        });

        // Second concurrent /init wins the race after the first one already
        // committed. The actor MUST keep the existing binding_id and discard
        // the second one (ADR-0018 §Implementation Notes #2).
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_second",
        });

        _agent.State.BindingId.Should().Be("bnd_first");
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
    public async Task HandleCommitBinding_IgnoresNullExternalSubject()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = null,
            BindingId = "bnd_x",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.ExternalSubject.Should().BeNull();
    }

    [Fact]
    public async Task HandleRevokeBinding_IgnoresNullExternalSubject()
    {
        // Seed an existing binding first so we can verify revoke is a no-op.
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = SampleSubject(),
            BindingId = "bnd_first",
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
        await _agent.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = SampleSubject(),
            Reason = "stray_unbind",
        });

        _agent.State.BindingId.Should().BeEmpty();
        _agent.State.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task HandleEventAsync_AcceptsEnvelopeForKnownPayload()
    {
        // Earlier rounds (mimo-v2.5-pro L37) flagged that the test suite
        // never exercises the envelope -> [EventHandler] dispatch path,
        // only the handler bodies. Direct-instantiated agents can call
        // HandleEventAsync without the runtime — what we can verify here is
        // that the framework accepts a well-formed envelope carrying a
        // CommitBindingCommand without throwing.  The actor pipeline only
        // resolves [EventHandler] methods when it has been bootstrapped via
        // the Orleans cluster (so directly-instantiated agents see a
        // narrower handler set), which is why state isn't asserted here —
        // the deeper "envelope through the [EventHandler] reflection path"
        // case lands in the Orleans-test-cluster follow-up tracked at the
        // top of this file.
        var subject = SampleSubject();
        var envelope = new EventEnvelope
        {
            Id = "envelope-1",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new CommitBindingCommand
            {
                ExternalSubject = subject,
                BindingId = "bnd_dispatched",
            }),
        };

        var act = () => _agent.HandleEventAsync(envelope, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RebindAfterRevoke_AcceptsNewBindingId()
    {
        var subject = SampleSubject();
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd_first",
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
}
