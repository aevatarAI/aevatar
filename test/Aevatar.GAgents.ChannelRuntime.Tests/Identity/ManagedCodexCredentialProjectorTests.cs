using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ManagedCodexCredentialProjectorTests
{
    [Fact]
    public async Task ProjectAsync_UsesCommittedActorVersionAndCopiesNoRawSecret()
    {
        var dispatcher = new RecordingDispatcher();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-21T12:00:00Z"));
        var projector = new ManagedCodexCredentialProjector(dispatcher, clock);
        var subject = new ExternalSubjectRef
        {
            Platform = "nyxid",
            Tenant = "tenant-a",
            ExternalUserId = "user-a",
        };
        var actorId = ManagedCodexCredentialActorIdentity.From(subject);
        var state = new ManagedCodexCredentialState
        {
            Credential = new ManagedCodexCredentialDescriptor
            {
                Owner = subject,
                ApiKeyId = "key-1",
                SecretReference = new SecretReference
                {
                    Ref = "sec-1",
                    Purpose = "managed.codex-invocation-agent-key",
                    Fingerprint = "fingerprint",
                    Version = 1,
                    OwnerScopeKey = actorId,
                },
                ChronoSandboxUserServiceId = "user-service-sandbox",
                ChronoLlmUserServiceId = "user-service-llm",
                ChronoSandboxServiceSlug = "chrono-sandbox",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-21T00:00:00Z")),
                Status = ManagedCodexCredentialStatus.Active,
            },
        };
        state.PendingRevocations.Add(new ManagedCodexCredentialCleanup
        {
            ApiKeyId = "key-old",
            SecretRef = "sec-old",
            NyxIdPending = true,
        });
        var envelope = TestEnvelopeBuilder.BuildCommittedEnvelope(state, version: 7, eventId: "event-7");

        await projector.ProjectAsync(
            new ManagedCodexCredentialMaterializationContext
            {
                RootActorId = actorId,
                ProjectionKind = "managed-codex-credential",
            },
            envelope);

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];
        document.Id.Should().Be(actorId);
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("event-7");
        document.Credential.ApiKeyId.Should().Be("key-1");
        document.Credential.ChronoLlmUserServiceId.Should().Be("user-service-llm");
        document.PendingRevocations.Should().ContainSingle();
        document.ToString().Should().NotContain("raw-agent-key");
    }

    private sealed class FixedClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class RecordingDispatcher : IProjectionWriteDispatcher<ManagedCodexCredentialDocument>
    {
        public List<ManagedCodexCredentialDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ManagedCodexCredentialDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }
}
