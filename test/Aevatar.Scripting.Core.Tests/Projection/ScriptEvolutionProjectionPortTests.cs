using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection;
using Aevatar.Scripting.Projection.Configuration;
using Aevatar.Scripting.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptEvolutionProjectionPortTests
{
    [Fact]
    public async Task AttachExistingActorProjectionAsync_ShouldAttachOnlyWhenProjectionSessionExists()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:script-evolution-session:session-1:proposal-1");
        var port = new ScriptEvolutionProjectionPort(
            new ScriptEvolutionProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingCompletedEventSink();

        var attachment = await port.AttachExistingActorProjectionAsync("session-1", "proposal-1", sink);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.ActorId.Should().Be("session-1");
        attachment.ProjectionLease.ProposalId.Should().Be("proposal-1");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("session-1");
        hub.LastSessionId.Should().Be("proposal-1");
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:script-evolution-session:session-1:proposal-1");
    }

    [Fact]
    public async Task AttachExistingActorProjectionAsync_ShouldReturnNull_WhenProjectionSessionIsCold()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        var port = new ScriptEvolutionProjectionPort(
            new ScriptEvolutionProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));

        var attachment = await port.AttachExistingActorProjectionAsync(
            "session-1",
            "proposal-1",
            new RecordingCompletedEventSink());

        attachment.Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:script-evolution-session:session-1:proposal-1");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existingActors = new(StringComparer.Ordinal);

        public List<string> ExistsCalls { get; } = [];

        public void MarkExists(string actorId) => _existingActors.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCalls.Add(id);
            return Task.FromResult(_existingActors.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<ScriptEvolutionRuntimeLease>
    {
        public Task ReleaseIfIdleAsync(ScriptEvolutionRuntimeLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static IProjectionScopeAttachExistingLeaseLookup<ScriptEvolutionRuntimeLease> CreateAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext>(
            runtime,
            request => new ScriptEvolutionSessionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            (_, context) => new ScriptEvolutionRuntimeLease(context));

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>
    {
        public int SubscribeCalls { get; private set; }

        public string? LastRootActorId { get; private set; }

        public string? LastSessionId { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            ScriptEvolutionSessionCompletedEvent evt,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<ScriptEvolutionSessionCompletedEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastRootActorId = rootActorId;
            LastSessionId = sessionId;
            return Task.FromResult<IAsyncDisposable>(new RecordingSubscription());
        }
    }

    private sealed class RecordingSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingCompletedEventSink : IEventSink<ScriptEvolutionSessionCompletedEvent>
    {
        public void Push(ScriptEvolutionSessionCompletedEvent evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(ScriptEvolutionSessionCompletedEvent evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
