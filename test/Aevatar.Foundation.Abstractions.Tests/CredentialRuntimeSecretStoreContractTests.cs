using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class CredentialRuntimeSecretStoreContractTests
{
    [Fact]
    public async Task InMemoryRuntimeSecretStore_ShouldExpireByInjectedClockAndEnforceOwnerPurpose()
    {
        var clock = new ManualRuntimeSecretClock(1_000);
        var store = new InMemoryRuntimeSecretStore(clock);

        var stored = (await store.PutAsync(new StoreRuntimeSecretRequest(
            Purpose: CredentialSecretPurposes.WorkflowCallerBearerToken,
            OwnerRunId: "run-a",
            OwnerStepId: "step-a",
            Secret: "caller-token",
            TimeToLive: TimeSpan.FromMilliseconds(50),
            ConsumeOnce: false,
            AuditReason: "start workflow"))).Reference;

        stored.Ref.ShouldNotBeNullOrWhiteSpace();
        stored.Ref.ShouldNotContain("caller-token");
        stored.Ref.ShouldNotContain("run-a");
        stored.Ref.ShouldNotContain("step-a");
        stored.ExpiresAtUnixMs.ShouldBe(1_050);

        var resolved = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-a",
            "step-a",
            "provider call"));
        resolved.Secret.ShouldBe("caller-token");

        var wrongStep = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-a",
            "step-b",
            "wrong step"));
        wrongStep.Secret.ShouldBeNull();

        clock.Advance(TimeSpan.FromMilliseconds(51));
        var expired = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-a",
            "step-a",
            "after ttl"));
        expired.Secret.ShouldBeNull();
    }

    [Fact]
    public async Task InMemoryRuntimeSecretStore_ShouldConsumeOnceAndRevoke()
    {
        var clock = new ManualRuntimeSecretClock(10_000);
        var store = new InMemoryRuntimeSecretStore(clock);

        var stored = (await store.PutAsync(new StoreRuntimeSecretRequest(
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "run-a",
            "step-a",
            "secure-value",
            TimeSpan.FromMinutes(5),
            ConsumeOnce: true,
            AuditReason: "capture secure input"))).Reference;

        var first = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "run-a",
            "step-a",
            "first use"));
        first.Secret.ShouldBe("secure-value");

        await store.ConsumeAsync(new ConsumeRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "run-a",
            "step-a",
            "mark used"));

        var second = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Ref,
            CredentialSecretPurposes.WorkflowSecureInputValue,
            "run-a",
            "step-a",
            "second use"));
        second.Secret.ShouldBeNull();

        var persistent = (await store.PutAsync(new StoreRuntimeSecretRequest(
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-b",
            "step-b",
            "persistent-runtime-token",
            TimeSpan.FromMinutes(5),
            ConsumeOnce: false,
            AuditReason: "start workflow"))).Reference;

        await store.RevokeAsync(new RevokeRuntimeSecretRequest(
            persistent.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-b",
            "step-b",
            "terminal cleanup"));

        var revoked = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            persistent.Ref,
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            "run-b",
            "step-b",
            "after revoke"));
        revoked.Secret.ShouldBeNull();
    }
}
