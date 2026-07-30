using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Identity;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ExternalIdentityBindingAuditTranslatorTests
{
    private const string RawUser = "USER-RAW-open-user-id-999";
    private const string SecretBindingId = "nyx-binding-SECRET-abc";
    private const string SubjectActorId = "external-identity-binding:lark:tenant-1:" + RawUser;

    [Fact]
    public void BoundTranslator_HashesSubject_AndNeverLeaksSubjectOrBindingId()
    {
        var hasher = CreateHasher();
        var evt = new ExternalIdentityBoundEvent
        {
            ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "tenant-1", ExternalUserId = RawUser },
            BindingId = SecretBindingId,
        };

        var record = new ExternalIdentityBoundAuditTranslator(hasher)
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("identity.external-binding.bound");
        record.Target.Kind.Should().Be("external_identity_binding");
        record.CommittedFactRef.ActorId.Should().Be(hasher.Hash(SubjectActorId).AuditActorId);
        record.CommittedFactRef.ActorId.Should().StartWith("audit_actor:hmac-sha256:");
        record.Annotations.Should().Contain("platform", "lark");
        record.Annotations.Should().ContainKey("origin_actor_identity_key_id");

        var serialized = System.Text.Encoding.UTF8.GetString(record.ToByteArray()) + record.ToString();
        serialized.Should().NotContain(RawUser);
        serialized.Should().NotContain(SecretBindingId);
    }

    [Fact]
    public void RevokedTranslator_IsDestructiveRestricted_RecordsReason_AndHidesSubject()
    {
        var hasher = CreateHasher();
        var evt = new ExternalIdentityBindingRevokedEvent
        {
            ExternalSubject = new ExternalSubjectRef { Platform = "telegram", Tenant = "", ExternalUserId = RawUser },
            Reason = "user_unbind",
        };

        var record = new ExternalIdentityBindingRevokedAuditTranslator(hasher)
            .Translate(Context("external-identity-binding:telegram::" + RawUser), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("identity.external-binding.revoked");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
        record.Annotations.Should().Contain("reason", "user_unbind");
        record.Annotations.Should().Contain("platform", "telegram");
        record.ToString().Should().NotContain(RawUser);
    }

    [Fact]
    public void ReplacedTranslator_RecordsReason_AndNeverLeaksEitherBindingId()
    {
        var hasher = CreateHasher();
        var evt = new ExternalIdentityBindingReplacedEvent
        {
            ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "tenant-1", ExternalUserId = RawUser },
            PreviousBindingId = SecretBindingId,
            BindingId = "nyx-binding-SECRET-next",
            Reason = "studio_service_access_review",
        };

        var record = new ExternalIdentityBindingReplacedAuditTranslator(hasher)
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("identity.external-binding.replaced");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("reason", "studio_service_access_review");
        var serialized = System.Text.Encoding.UTF8.GetString(record.ToByteArray()) + record;
        serialized.Should().NotContain(RawUser);
        serialized.Should().NotContain(SecretBindingId);
        serialized.Should().NotContain("nyx-binding-SECRET-next");
    }

    [Fact]
    public void BoundTranslator_WhenNoHasher_SkipsInsteadOfLeaking()
    {
        var evt = new ExternalIdentityBoundEvent
        {
            ExternalSubject = new ExternalSubjectRef { Platform = "lark", Tenant = "tenant-1", ExternalUserId = RawUser },
        };

        new ExternalIdentityBoundAuditTranslator(actorIdentityHasher: null)
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .BeEmpty();
    }

    private static CommittedAuditTranslationContext Context(string originActorId = SubjectActorId) =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = originActorId, EventId = "event-1", Version = 3 },
            originActorId,
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "corr-1");

    private static AuditActorIdentityHasher CreateHasher() =>
        new(Options.Create(new AuditActorIdentityHasherOptions
        {
            ActiveKeyId = "key-1",
            Keys =
            [
                new AuditActorIdentityHasherKeyOptions
                {
                    KeyId = "key-1",
                    Key = "active secret material for audit identity hashing",
                },
            ],
        }));
}
