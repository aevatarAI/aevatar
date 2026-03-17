using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ProjectionScriptAuthorityReadModelActivationPortTests
{
    [Fact]
    public async Task ActivateAsync_ShouldEnsureProjectionForActor()
    {
        var activationService = new StubActivationService(new object());
        var projectionPort = CreateProjectionPort(activationService);
        var port = new ProjectionScriptAuthorityReadModelActivationPort(projectionPort);

        await port.ActivateAsync("script-definition:script-1", CancellationToken.None);

        activationService.ActorIds.Should().Equal("script-definition:script-1");
    }

    [Fact]
    public async Task ActivateAsync_ShouldThrow_WhenProjectionDisabled()
    {
        var projectionPort = CreateProjectionPort(new StubActivationService(null));
        var port = new ProjectionScriptAuthorityReadModelActivationPort(projectionPort);

        var action = () => port.ActivateAsync("script-definition:script-2", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script-definition:script-2*");
    }

    private static ScriptAuthorityProjectionPortService CreateProjectionPort(
        StubActivationService activationService) =>
        new(
            activationService,
            new StubReleaseService(),
            new StubSinkSubscriptionManager(),
            new StubLiveForwarder());

    private sealed class StubActivationService(object? leaseMarker)
        : IProjectionPortActivationService<ScriptAuthorityRuntimeLease>
    {
        public List<string> ActorIds { get; } = [];

        public Task<ScriptAuthorityRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            ActorIds.Add(rootEntityId);
            if (leaseMarker is null)
                return Task.FromResult<ScriptAuthorityRuntimeLease>(null!);

            var context = new ScriptAuthorityProjectionContext
            {
                ProjectionId = $"{rootEntityId}:authority",
                RootActorId = rootEntityId,
            };

            return Task.FromResult(new ScriptAuthorityRuntimeLease(context));
        }
    }

    private sealed class StubReleaseService : IProjectionPortReleaseService<ScriptAuthorityRuntimeLease>
    {
        public Task ReleaseIfIdleAsync(ScriptAuthorityRuntimeLease runtimeLease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSinkSubscriptionManager
        : IEventSinkProjectionSubscriptionManager<ScriptAuthorityRuntimeLease, EventEnvelope>
    {
        public Task AttachOrReplaceAsync(
            ScriptAuthorityRuntimeLease lease,
            IEventSink<EventEnvelope> sink,
            Func<EventEnvelope, ValueTask> handler,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DetachAsync(
            ScriptAuthorityRuntimeLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubLiveForwarder
        : IEventSinkProjectionLiveForwarder<ScriptAuthorityRuntimeLease, EventEnvelope>
    {
        public ValueTask ForwardAsync(
            ScriptAuthorityRuntimeLease lease,
            IEventSink<EventEnvelope> sink,
            EventEnvelope evt,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
