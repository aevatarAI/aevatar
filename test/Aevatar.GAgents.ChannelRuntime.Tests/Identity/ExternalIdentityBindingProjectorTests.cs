using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins behaviour of <see cref="ExternalIdentityBindingProjector"/>: the
/// projector materializes the typed actor state into one document per actor,
/// keyed by the deterministic actor id, and ignores envelopes that do not
/// carry a committed binding state.
/// </summary>
public class ExternalIdentityBindingProjectorTests
{
    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public async Task ProjectAsync_IgnoresInvalidEnvelope()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new ExternalIdentityBindingProjector(dispatcher, new FixedClock(DateTimeOffset.UtcNow));
        var context = new ExternalIdentityBindingMaterializationContext
        {
            RootActorId = SampleSubject().ToActorId(),
            ProjectionKind = "external-identity-binding",
        };

        await projector.ProjectAsync(context, new EventEnvelope());

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WritesActiveBindingDocument()
    {
        var dispatcher = new RecordingDispatcher();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var projector = new ExternalIdentityBindingProjector(dispatcher, clock);
        var subject = SampleSubject();
        var context = new ExternalIdentityBindingMaterializationContext
        {
            RootActorId = subject.ToActorId(),
            ProjectionKind = "external-identity-binding",
        };

        var state = new ExternalIdentityBindingState
        {
            ExternalSubject = subject,
            BindingId = "bnd_active",
            BoundAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-29T09:30:00Z")),
        };
        var envelope = TestEnvelopeBuilder.BuildCommittedEnvelope(state, version: 1, eventId: "ev-1");

        await projector.ProjectAsync(context, envelope);

        dispatcher.Upserts.Should().HaveCount(1);
        var doc = dispatcher.Upserts[0];
        doc.Id.Should().Be(subject.ToActorId());
        doc.BindingId.Should().Be("bnd_active");
        doc.IsActive.Should().BeTrue();
        doc.RevokedAtUtcValue.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_WritesRevokedDocumentAsInactive()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new ExternalIdentityBindingProjector(dispatcher, new FixedClock(DateTimeOffset.UtcNow));
        var subject = SampleSubject();
        var context = new ExternalIdentityBindingMaterializationContext
        {
            RootActorId = subject.ToActorId(),
            ProjectionKind = "external-identity-binding",
        };

        var state = new ExternalIdentityBindingState
        {
            ExternalSubject = subject,
            BindingId = string.Empty,
            BoundAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-5)),
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var envelope = TestEnvelopeBuilder.BuildCommittedEnvelope(state, version: 2, eventId: "ev-2");

        await projector.ProjectAsync(context, envelope);

        dispatcher.Upserts.Should().HaveCount(1);
        var doc = dispatcher.Upserts[0];
        doc.IsActive.Should().BeFalse();
        doc.BindingId.Should().BeEmpty();
        doc.RevokedAtUtcValue.Should().NotBeNull();
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingDispatcher : IProjectionWriteDispatcher<ExternalIdentityBindingDocument>
    {
        public List<ExternalIdentityBindingDocument> Upserts { get; } = new();

        public Task<ProjectionWriteResult> UpsertAsync(
            ExternalIdentityBindingDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ProjectionWriteResult.Applied());
    }
}
