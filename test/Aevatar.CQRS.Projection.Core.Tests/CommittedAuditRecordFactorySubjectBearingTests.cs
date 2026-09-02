using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Identity;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class CommittedAuditRecordFactorySubjectBearingTests
{
    // A subject-bearing actor id: the ExternalIdentityBindingGAgent keys itself
    // as "external-identity-binding:{platform}:{tenant}:{external_user_id}", so
    // the raw external subject is embedded in the actor id.
    private const string RawSubject = "USER-RAW-abc123";
    private const string SubjectBearingActorId = "external-identity-binding:lark:tenant-9:" + RawSubject;

    [Fact]
    public void CreateSystemRecord_WhenSubjectBearing_HashesOriginActorIdAndNeverLeaksRawSubject()
    {
        var hasher = CreateHasher();
        var context = Context(SubjectBearingActorId);
        var seed = new CommittedAuditSeed(
            "identity.binding.bound",
            "external_identity_binding",
            "target-1",
            "scope-1",
            SubjectBearing: true);

        var record = CommittedAuditRecordFactory.CreateSystemRecord(context, seed, hasher);

        var expected = hasher.Hash(SubjectBearingActorId);
        record.CommittedFactRef.ActorId.Should().Be(expected.AuditActorId);
        record.CommittedFactRef.ActorId.Should().StartWith("audit_actor:hmac-sha256:");
        record.Annotations["origin_actor_id"].Should().Be(expected.AuditActorId);
        record.Annotations["origin_actor_identity_key_id"].Should().Be(expected.IdentityKeyId);

        // The raw external subject must not appear anywhere in the artifact.
        record.ToString().Should().NotContain(RawSubject);
        System.Text.Encoding.UTF8.GetString(record.ToByteArray()).Should().NotContain(RawSubject);
    }

    [Fact]
    public void CreateSystemRecord_WhenSubjectBearingWithoutHasher_Throws()
    {
        var context = Context(SubjectBearingActorId);
        var seed = new CommittedAuditSeed("identity.binding.bound", "external_identity_binding", "target-1", "scope-1", SubjectBearing: true);

        Action act = () => CommittedAuditRecordFactory.CreateSystemRecord(context, seed, actorIdentityHasher: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*requires an*IAuditActorIdentityHasher*");
    }

    [Fact]
    public void CreateSystemRecord_WhenNotSubjectBearing_KeepsRawOriginActorIdForCorrelation()
    {
        var context = Context("service:tenant/app:svc-1");
        var seed = new CommittedAuditSeed("service.updated", "service", "svc-1", "scope-1");

        var record = CommittedAuditRecordFactory.CreateSystemRecord(context, seed);

        record.CommittedFactRef.ActorId.Should().Be("service:tenant/app:svc-1");
        record.Annotations["origin_actor_id"].Should().Be("service:tenant/app:svc-1");
        record.Annotations.Should().NotContainKey("origin_actor_identity_key_id");
    }

    [Fact]
    public void SubjectBearingTranslatorBase_MarksSeedAndHashesThroughFactory()
    {
        var hasher = CreateHasher();
        var translator = new TestSubjectBearingTranslator(hasher);

        var record = translator
            .Translate(Context(SubjectBearingActorId), Any.Pack(new StringValue { Value = "ignored" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.CommittedFactRef.ActorId.Should().Be(hasher.Hash(SubjectBearingActorId).AuditActorId);
        record.ToString().Should().NotContain(RawSubject);
    }

    [Fact]
    public void SubjectBearingTranslatorBase_WhenNoHasherConfigured_SkipsRecordInsteadOfLeaking()
    {
        var translator = new TestSubjectBearingTranslator(hasher: null);

        translator
            .Translate(Context(SubjectBearingActorId), Any.Pack(new StringValue { Value = "ignored" }))
            .Should()
            .BeEmpty();
    }

    private sealed class TestSubjectBearingTranslator : SubjectBearingCommittedAuditTranslatorBase<StringValue>
    {
        public TestSubjectBearingTranslator(AuditActorIdentityHasher? hasher)
            : base(hasher)
        {
        }

        public override string EventTypeUrl => AuditCommittedEventTypeUrl.FromDescriptor(StringValue.Descriptor);

        protected override CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, StringValue evt) =>
            new("identity.binding.bound", "external_identity_binding", "target-1", "scope-1");
    }

    private static CommittedAuditTranslationContext Context(string originActorId) =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = originActorId, EventId = "event-1", Version = 5 },
            originActorId,
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-09T09:00:00+00:00"),
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
