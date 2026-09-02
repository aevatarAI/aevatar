using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexCredentialReadinessAssessmentTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-28T00:00:00Z");

    [Fact]
    public void Assess_WhenDescriptorIsComplete_ReturnsReady()
    {
        var assessment = Assess();

        assessment.ExecutionReady.Should().BeTrue();
        assessment.Reason.Should().Be("ready");
    }

    [Fact]
    public void Assess_WhenTargetIsDisabled_ReturnsTargetDisabledBeforeDescriptorChecks()
    {
        var options = Options();
        options.Enabled = false;

        var assessment = Assess(options, snapshot: null);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_target_disabled");
    }

    [Fact]
    public void Assess_WhenOwnerIsIneligible_ReturnsFeatureNotEnabledBeforeDescriptorChecks()
    {
        var assessment = ManagedCodexCredentialReadiness.Assess(
            Options(),
            Owner("user-b"),
            snapshot: null,
            Now);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_feature_not_enabled");
    }

    [Fact]
    public void Assess_WhenDescriptorIsMissing_ReturnsNotProvisioned()
    {
        var assessment = ManagedCodexCredentialReadiness.Assess(
            Options(),
            Owner("user-a"),
            snapshot: null,
            Now);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_not_provisioned");
    }

    [Fact]
    public void Assess_WhenDescriptorIsInactive_ReturnsInactive()
    {
        var snapshot = Snapshot();
        snapshot.Credential.Status = ManagedCodexCredentialStatus.Revoked;

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_inactive");
    }

    [Fact]
    public void Assess_WhenExpiryIsMissing_ReturnsExpired()
    {
        var snapshot = Snapshot();
        snapshot.Credential.ExpiresAt = null;

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_expired");
    }

    [Fact]
    public void Assess_WhenExpiryHasPassed_ReturnsExpired()
    {
        var snapshot = Snapshot();
        var expiresAt = Now.AddMilliseconds(-1);
        snapshot.Credential.ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt);
        snapshot.Credential.SecretReference.ExpiresAtUnixMs =
            expiresAt.ToUnixTimeMilliseconds();

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_expired");
    }

    [Theory]
    [InlineData(OwnerFault.MissingOwner)]
    [InlineData(OwnerFault.WrongUser)]
    [InlineData(OwnerFault.WrongPlatform)]
    [InlineData(OwnerFault.NonEmptyTenant)]
    public void Assess_WhenOwnerIsInvalid_ReturnsOwnerInvalid(OwnerFault fault)
    {
        var snapshot = Snapshot();
        snapshot.Credential.Owner = fault switch
        {
            OwnerFault.MissingOwner => null,
            OwnerFault.WrongUser => Owner("user-b"),
            OwnerFault.WrongPlatform => new ExternalSubjectRef
            {
                Platform = "lark",
                ExternalUserId = "user-a",
            },
            OwnerFault.NonEmptyTenant => new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = "tenant-a",
                ExternalUserId = "user-a",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null),
        };

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_owner_invalid");
    }

    [Theory]
    [InlineData(ReferenceFault.MissingReference)]
    [InlineData(ReferenceFault.BlankRef)]
    [InlineData(ReferenceFault.WrongPurpose)]
    [InlineData(ReferenceFault.WrongOwnerScope)]
    [InlineData(ReferenceFault.InvalidVersion)]
    [InlineData(ReferenceFault.BlankFingerprint)]
    [InlineData(ReferenceFault.WrongExpiry)]
    public void Assess_WhenVaultReferenceIsInvalid_ReturnsReferenceInvalid(
        ReferenceFault fault)
    {
        var snapshot = Snapshot();
        var reference = snapshot.Credential.SecretReference;
        switch (fault)
        {
            case ReferenceFault.MissingReference:
                snapshot.Credential.SecretReference = null;
                break;
            case ReferenceFault.BlankRef:
                reference.Ref = " ";
                break;
            case ReferenceFault.WrongPurpose:
                reference.Purpose = "other-purpose";
                break;
            case ReferenceFault.WrongOwnerScope:
                reference.OwnerScopeKey =
                    "managed-codex-credential:nyxid::user-b";
                break;
            case ReferenceFault.InvalidVersion:
                reference.Version = 0;
                break;
            case ReferenceFault.BlankFingerprint:
                reference.Fingerprint = " ";
                break;
            case ReferenceFault.WrongExpiry:
                reference.ExpiresAtUnixMs = Now.AddDays(29).ToUnixTimeMilliseconds();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_reference_invalid");
    }

    [Theory]
    [InlineData(ServiceBindingFault.BlankApiKeyId)]
    [InlineData(ServiceBindingFault.BlankSandboxUserServiceId)]
    [InlineData(ServiceBindingFault.BlankLlmUserServiceId)]
    [InlineData(ServiceBindingFault.EqualUserServiceIds)]
    [InlineData(ServiceBindingFault.WrongSandboxSlug)]
    public void Assess_WhenServiceBindingIsInvalid_ReturnsServiceBindingInvalid(
        ServiceBindingFault fault)
    {
        var snapshot = Snapshot();
        switch (fault)
        {
            case ServiceBindingFault.BlankApiKeyId:
                snapshot.Credential.ApiKeyId = " ";
                break;
            case ServiceBindingFault.BlankSandboxUserServiceId:
                snapshot.Credential.ChronoSandboxUserServiceId = " ";
                break;
            case ServiceBindingFault.BlankLlmUserServiceId:
                snapshot.Credential.ChronoLlmUserServiceId = " ";
                break;
            case ServiceBindingFault.EqualUserServiceIds:
                snapshot.Credential.ChronoLlmUserServiceId =
                    snapshot.Credential.ChronoSandboxUserServiceId;
                break;
            case ServiceBindingFault.WrongSandboxSlug:
                snapshot.Credential.ChronoSandboxServiceSlug = "sandbox-alias";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }

        var assessment = Assess(snapshot: snapshot);

        assessment.ExecutionReady.Should().BeFalse();
        assessment.Reason.Should().Be("managed_credential_service_binding_invalid");
    }

    [Fact]
    public void Assess_DoesNotPutCredentialIdentityOrVaultDetailsInMessage()
    {
        var snapshot = Snapshot();
        snapshot.Credential.SecretReference.Purpose = "other-purpose";

        var assessment = Assess(snapshot: snapshot);

        assessment.Message.Should().NotContain("key-a");
        assessment.Message.Should().NotContain("sec-a");
        assessment.Message.Should().NotContain("fingerprint-a");
    }

    private static ManagedCodexCredentialReadinessAssessment Assess(
        ManagedCodexOptions? options = null,
        ManagedCodexCredentialSnapshot? snapshot = null) =>
        ManagedCodexCredentialReadiness.Assess(
            options ?? Options(),
            Owner("user-a"),
            snapshot ?? Snapshot(),
            Now);

    private static ManagedCodexOptions Options() => new()
    {
        Enabled = true,
        RolloutBoundary = ManagedCodexRolloutBoundary.InternalOnly,
        Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a"],
        },
    };

    private static ManagedCodexCredentialSnapshot Snapshot()
    {
        var expiresAt = Now.AddDays(30);
        return new ManagedCodexCredentialSnapshot
        {
            Credential = new ManagedCodexCredentialDescriptor
            {
                Owner = Owner("user-a"),
                ApiKeyId = "key-a",
                SecretReference = new SecretReference
                {
                    Ref = "sec-a",
                    Purpose = CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    OwnerScopeKey = "managed-codex-credential:nyxid::user-a",
                    Fingerprint = "fingerprint-a",
                    Version = 1,
                    ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
                },
                ChronoSandboxUserServiceId = "us-sandbox",
                ChronoLlmUserServiceId = "us-llm",
                ChronoSandboxServiceSlug = ManagedCodexOptions.ChronoSandboxServiceSlug,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
                Status = ManagedCodexCredentialStatus.Active,
            },
            StateVersion = 7,
            LastEventId = "event-7",
        };
    }

    private static ExternalSubjectRef Owner(string userId) => new()
    {
        Platform = OwnerScope.NyxIdPlatform,
        Tenant = string.Empty,
        ExternalUserId = userId,
    };

    public enum OwnerFault
    {
        MissingOwner,
        WrongUser,
        WrongPlatform,
        NonEmptyTenant,
    }

    public enum ReferenceFault
    {
        MissingReference,
        BlankRef,
        WrongPurpose,
        WrongOwnerScope,
        InvalidVersion,
        BlankFingerprint,
        WrongExpiry,
    }

    public enum ServiceBindingFault
    {
        BlankApiKeyId,
        BlankSandboxUserServiceId,
        BlankLlmUserServiceId,
        EqualUserServiceIds,
        WrongSandboxSlug,
    }
}
