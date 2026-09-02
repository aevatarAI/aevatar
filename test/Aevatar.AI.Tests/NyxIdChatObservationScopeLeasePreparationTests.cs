using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatObservationScopeLeasePreparationTests
{
    [Fact]
    public async Task PrepareAsync_ShouldActivateSessionObservationScopeForChatCommand()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var preparation = new NyxIdChatObservationScopeLeasePreparation<NyxIdChatCommand>(
            activation,
            release,
            static command => command.TurnId);
        var execution = CreateExecution(" actor-1 ", "cmd-1", " session-1 ");

        var result = await preparation.PrepareAsync(
            new NyxIdChatCommand("actor-1", "scope-1", "hello", " session-1 ", "token", null, null),
            execution,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Handle.Should().NotBeNull();
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-1");
        activation.Requests[0].ProjectionKind.Should().Be("nyxid-chat-session");
        activation.Requests[0].Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);
        activation.Requests[0].SessionId.Should().Be("session-1");

        await result.Handle!.ReleaseAsync(CancellationToken.None);

        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(activation.Leases[0]);
    }

    [Fact]
    public async Task PrepareAsync_ShouldActivateSessionObservationScopeForApprovalCommand()
    {
        var activation = new RecordingActivationService();
        var preparation = new NyxIdChatObservationScopeLeasePreparation<NyxIdApprovalCommand>(
            activation,
            new RecordingReleaseService(),
            static command => command.TurnId);
        var execution = CreateExecution("actor-2", "cmd-2", "session-2");

        var result = await preparation.PrepareAsync(
            new NyxIdApprovalCommand("actor-2", "request-1", true, "approved", "session-2"),
            execution,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-2");
        activation.Requests[0].SessionId.Should().Be("session-2");
    }

    [Theory]
    [InlineData("", "session-1")]
    [InlineData("   ", "session-1")]
    [InlineData("actor-1", "")]
    [InlineData("actor-1", "   ")]
    public async Task PrepareAsync_ShouldReturnProjectionUnavailableForBlankIdentifiers(
        string actorId,
        string sessionId)
    {
        var activation = new RecordingActivationService();
        var preparation = new NyxIdChatObservationScopeLeasePreparation<NyxIdChatCommand>(
            activation,
            new RecordingReleaseService(),
            static command => command.TurnId);

        var result = await preparation.PrepareAsync(
            new NyxIdChatCommand(actorId, "scope-1", "hello", sessionId, "token", null, null),
            CreateExecution(actorId, "cmd-1", sessionId),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatStartError.ProjectionUnavailable);
        result.Handle.Should().BeNull();
        activation.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ShouldReturnProjectionUnavailable_WhenActivationFails()
    {
        var activation = new RecordingActivationService { ThrowOnEnsure = true };
        var release = new RecordingReleaseService();
        var preparation = new NyxIdChatObservationScopeLeasePreparation<NyxIdChatCommand>(
            activation,
            release,
            static command => command.TurnId);

        var result = await preparation.PrepareAsync(
            new NyxIdChatCommand("actor-1", "scope-1", "hello", "session-1", "token", null, null),
            CreateExecution("actor-1", "cmd-1", "session-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatStartError.ProjectionUnavailable);
        result.Handle.Should().BeNull();
        release.Leases.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ShouldHonorCancellation()
    {
        var preparation = new NyxIdChatObservationScopeLeasePreparation<NyxIdChatCommand>(
            new RecordingActivationService(),
            new RecordingReleaseService(),
            static command => command.TurnId);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await preparation.PrepareAsync(
            new NyxIdChatCommand("actor-1", "scope-1", "hello", "session-1", "token", null, null),
            CreateExecution("actor-1", "cmd-1", "session-1"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CommandDispatchExecution<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt> CreateExecution(
        string actorId,
        string commandId,
        string sessionId)
    {
        var target = new NyxIdChatCommandTarget(
            new TestActor(actorId),
            new NoopProjectionPort());

        return new CommandDispatchExecution<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>
        {
            Target = target,
            Context = new CommandContext(actorId, commandId, "corr-1", new Dictionary<string, string>()),
            Envelope = new EventEnvelope { Id = $"env-{commandId}" },
            Receipt = new NyxIdChatAcceptedReceipt(actorId, commandId, "corr-1", sessionId),
        };
    }

    private sealed class RecordingActivationService : IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>
    {
        public bool ThrowOnEnsure { get; init; }
        public List<ProjectionScopeStartRequest> Requests { get; } = [];
        public List<NyxIdChatSessionRuntimeLease> Leases { get; } = [];

        public Task<NyxIdChatSessionRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (ThrowOnEnsure)
                throw new InvalidOperationException("activation failed");

            var lease = new NyxIdChatSessionRuntimeLease(new NyxIdChatSessionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            });
            Leases.Add(lease);
            return Task.FromResult(lease);
        }
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease>
    {
        public List<NyxIdChatSessionRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(NyxIdChatSessionRuntimeLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopProjectionPort : INyxIdChatSessionProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
            string actorId,
            string sessionId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            INyxIdChatSessionProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReleaseActorProjectionAsync(
            INyxIdChatSessionProjectionLease lease,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeactivateAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string?> GetParentIdAsync() => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => throw new NotSupportedException();
    }
}
