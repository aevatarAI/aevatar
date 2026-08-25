using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class CredentialReferenceProtoContractTests
{
    [Fact]
    public void SecretReference_ShouldRoundTripTypedMetadataThroughProtobuf()
    {
        var reference = new SecretReference
        {
            Ref = "sec_opaque",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            Fingerprint = "sha256:abc",
            Version = 7,
            OwnerScopeKey = "scope-a",
            CreatedAtUnixMs = 123456,
            ExpiresAtUnixMs = 789012,
        };

        var parsed = SecretReference.Parser.ParseFrom(reference.ToByteArray());

        parsed.Ref.ShouldBe(reference.Ref);
        parsed.Purpose.ShouldBe(reference.Purpose);
        parsed.Fingerprint.ShouldBe(reference.Fingerprint);
        parsed.Version.ShouldBe(reference.Version);
        parsed.OwnerScopeKey.ShouldBe(reference.OwnerScopeKey);
        parsed.CreatedAtUnixMs.ShouldBe(reference.CreatedAtUnixMs);
        parsed.ExpiresAtUnixMs.ShouldBe(reference.ExpiresAtUnixMs);
    }

    [Fact]
    public void RuntimeSecretReference_ShouldRoundTripTypedMetadataThroughProtobuf()
    {
        var reference = new RuntimeSecretReference
        {
            Ref = "rsec_opaque",
            Purpose = CredentialSecretPurposes.WorkflowSecureInputValue,
            Fingerprint = "sha256:def",
            ExpiresAtUnixMs = 456789,
            ConsumeOnce = true,
            OwnerRunId = "run-a",
            OwnerStepId = "step-a",
        };

        var parsed = RuntimeSecretReference.Parser.ParseFrom(reference.ToByteArray());

        parsed.Ref.ShouldBe(reference.Ref);
        parsed.Purpose.ShouldBe(reference.Purpose);
        parsed.Fingerprint.ShouldBe(reference.Fingerprint);
        parsed.ExpiresAtUnixMs.ShouldBe(reference.ExpiresAtUnixMs);
        parsed.ConsumeOnce.ShouldBeTrue();
        parsed.OwnerRunId.ShouldBe(reference.OwnerRunId);
        parsed.OwnerStepId.ShouldBe(reference.OwnerStepId);
    }

    [Fact]
    public void DurableCallerCredentialRef_ShouldRoundTripTypedHandleThroughProtobuf()
    {
        var reference = new DurableCallerCredentialRef
        {
            Ref = "sec_scheduled",
            Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            OwnerScopeKey = "schedule:schedule-1",
            SubjectId = "lark:tenant:user-1",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
            ProviderCredentialId = "provider-key-1",
        };

        var parsed = DurableCallerCredentialRef.Parser.ParseFrom(reference.ToByteArray());

        parsed.Ref.ShouldBe(reference.Ref);
        parsed.Purpose.ShouldBe(reference.Purpose);
        parsed.OwnerScopeKey.ShouldBe(reference.OwnerScopeKey);
        parsed.SubjectId.ShouldBe(reference.SubjectId);
        parsed.SourceKind.ShouldBe(DurableCallerCredentialSourceKind.ScheduledDispatch);
        parsed.ProviderCredentialId.ShouldBe(reference.ProviderCredentialId);
    }

    [Fact]
    public void DurableCallerCredentialSourceKind_ShouldReserveChannelRegistrationIdentity()
    {
        ((int)DurableCallerCredentialSourceKind.ChannelRegistration).ShouldBe(4);
    }
}
