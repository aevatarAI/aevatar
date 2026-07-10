using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class CredentialSecretVaultContractTests
{
    [Fact]
    public async Task InMemorySecretVault_ShouldPersistOpaqueReferenceAndEnforceOwnerPurpose()
    {
        var vault = new InMemorySecretVault();

        var stored = (await vault.PutAsync(new StoreSecretRequest(
            Purpose: CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey: "scope-a",
            SubjectId: "schedule-1",
            Secret: "RAW_SECRET_SHOULD_NOT_APPEAR",
            AuditReason: "issue scheduled key"))).Reference;

        stored.Ref.ShouldNotBeNullOrWhiteSpace();
        stored.Ref.ShouldNotContain(CredentialSecretPurposes.ScheduledNyxApiKey);
        stored.Ref.ShouldNotContain("scope-a");
        stored.Ref.ShouldNotContain("schedule-1");
        stored.Fingerprint.ShouldNotBeNullOrWhiteSpace();
        stored.Version.ShouldBe(1);
        stored.OwnerScopeKey.ShouldBe("scope-a");

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "scope-a",
            "schedule-1",
            "deliver scheduled key"));

        resolved.Secret.ShouldBe("RAW_SECRET_SHOULD_NOT_APPEAR");

        var wrongOwner = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "scope-b",
            "schedule-1",
            "wrong owner"));
        wrongOwner.Secret.ShouldBeNull();

        var wrongPurpose = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "scope-a",
            "schedule-1",
            "wrong purpose"));
        wrongPurpose.Secret.ShouldBeNull();
    }

    [Fact]
    public async Task InMemorySecretVault_ShouldRotateAndRevokeByReference()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "scope-a",
            "run-1/step-1",
            "old-secret",
            "capture input"))).Reference;

        var rotated = (await vault.RotateAsync(new RotateSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "scope-a",
            "run-1/step-1",
            "new-secret",
            "rotate input"))).Reference;

        rotated.Ref.ShouldBe(stored.Ref);
        rotated.Version.ShouldBe(2);
        rotated.Fingerprint.ShouldNotBe(stored.Fingerprint);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "scope-a",
            "run-1/step-1",
            "use input"));
        resolved.Secret.ShouldBe("new-secret");

        await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "scope-a",
            "run-1/step-1",
            "terminal cleanup"));

        var revoked = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "scope-a",
            "run-1/step-1",
            "use after revoke"));
        revoked.Secret.ShouldBeNull();
    }

    [Fact]
    public async Task InMemorySecretVault_ShouldNotResolveExpiredSecret()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-a",
            "key-1",
            "expired-secret",
            "capture scheduled invocation key",
            DateTimeOffset.UtcNow.AddMinutes(-1)))).Reference;

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-a",
            "key-1",
            "dispatch scheduled invocation"));

        resolved.Secret.ShouldBeNull();
    }
}
