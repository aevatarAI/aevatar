using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the local-revoke reconcile path used when a turn observes
/// <c>BindingRevokedException</c> (NyxID invalid_grant). The reconciler must
/// event-source a RevokeBindingCommand to the owning actor with the supplied
/// reason and MUST NOT call the NyxID-side revoke (the grant is already gone
/// upstream).
/// </summary>
public sealed class BindingRevocationReconcilerTests
{
    [Fact]
    public async Task ReconcileRevokedAsync_ShouldInvalidateCatalogForNyxIdNativeOwner()
    {
        var lifecycle = new RecordingCatalogLifecycle();
        var reconciler = new BindingRevocationReconciler(
            new RecordingActorDispatchPort(),
            NullLogger<BindingRevocationReconciler>.Instance,
            lifecycle);
        var owner = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            ExternalUserId = "nyx-owner-alpha",
        };

        await reconciler.ReconcileRevokedAsync(owner, "nyx_invalid_grant");

        lifecycle.Subject!.ExternalUserId.Should().Be("nyx-owner-alpha");
        lifecycle.Reason.Should().Be("nyx_invalid_grant");
    }

    private static ExternalSubjectRef Subject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public async Task ReconcileRevokedAsync_DispatchesRevokeBindingCommand_ToSubjectActor()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var reconciler = new BindingRevocationReconciler(
            dispatchPort,
            NullLogger<BindingRevocationReconciler>.Instance);
        var subject = Subject();

        await reconciler.ReconcileRevokedAsync(subject, "nyx_invalid_grant", CancellationToken.None);

        dispatchPort.Dispatched.Should().ContainSingle("reconcile dispatches exactly one local revoke");
        var (actorId, envelope) = dispatchPort.Dispatched[0];
        actorId.Should().Be(subject.ToActorId());
        envelope.Route.Direct.TargetActorId.Should().Be(actorId);
        envelope.Route.PublisherActorId.Should().Be("channel.identity.reconcile");

        var revoke = envelope.Payload.Unpack<RevokeBindingCommand>();
        revoke.Reason.Should().Be("nyx_invalid_grant");
        revoke.ExternalSubject.Platform.Should().Be("lark");
        revoke.ExternalSubject.Tenant.Should().Be("ou_tenant_x");
        revoke.ExternalSubject.ExternalUserId.Should().Be("ou_user_y");
    }

    [Fact]
    public async Task ReconcileRevokedAsync_InvalidatesCatalogForNyxIdOwner()
    {
        var catalogLifecycle = new RecordingCatalogLifecycle();
        var reconciler = new BindingRevocationReconciler(
            new RecordingActorDispatchPort(),
            NullLogger<BindingRevocationReconciler>.Instance,
            catalogLifecycle);
        var owner = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = "tenant-alpha",
            ExternalUserId = "nyx-owner-alpha",
        };

        await reconciler.ReconcileRevokedAsync(owner, "binding_revoked", CancellationToken.None);

        catalogLifecycle.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            (owner, "binding_revoked"));
    }

    [Fact]
    public async Task ReconcileRevokedAsync_RetriesOnceOnTransientDispatchFailure()
    {
        var dispatchPort = new FailThenSucceedActorDispatchPort(failuresBeforeSuccess: 1);
        var reconciler = new BindingRevocationReconciler(
            dispatchPort,
            NullLogger<BindingRevocationReconciler>.Instance);

        await reconciler.ReconcileRevokedAsync(Subject(), "nyx_invalid_grant", CancellationToken.None);

        dispatchPort.AttemptCount.Should().Be(2, "first attempt fails, retry succeeds");
        dispatchPort.SucceededCount.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileRevokedAsync_SwallowsPersistentDispatchFailure_WithoutThrowing()
    {
        var dispatchPort = new ThrowingActorDispatchPort();
        var reconciler = new BindingRevocationReconciler(
            dispatchPort,
            NullLogger<BindingRevocationReconciler>.Instance);

        var act = async () => await reconciler.ReconcileRevokedAsync(Subject(), "nyx_invalid_grant", CancellationToken.None);

        await act.Should().NotThrowAsync("reconcile is best-effort and must not surface on the reply path");
        dispatchPort.AttemptCount.Should().Be(2, "both attempts are exhausted before giving up");
    }

    private sealed class FailThenSucceedActorDispatchPort : IActorDispatchPort
    {
        private readonly int _failuresBeforeSuccess;

        public FailThenSucceedActorDispatchPort(int failuresBeforeSuccess) =>
            _failuresBeforeSuccess = failuresBeforeSuccess;

        public int AttemptCount { get; private set; }
        public int SucceededCount { get; private set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            AttemptCount++;
            if (AttemptCount <= _failuresBeforeSuccess)
                throw new InvalidOperationException("simulated transient dispatch failure");

            SucceededCount++;
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ThrowingActorDispatchPort : IActorDispatchPort
    {
        public int AttemptCount { get; private set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            AttemptCount++;
            throw new InvalidOperationException("simulated dispatch failure");
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingCatalogLifecycle : INyxIdCatalogAccessLifecyclePort
    {
        public ExternalSubjectRef? Subject { get; private set; }
        public string? Reason { get; private set; }
        public List<(ExternalSubjectRef Subject, string Reason)> Requests { get; } = [];

        public Task InvalidateAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default)
        {
            Subject = subject;
            Reason = reason;
            Requests.Add((subject, reason));
            return Task.CompletedTask;
        }
    }
}
