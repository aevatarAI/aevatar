using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class StudioScheduledCredentialMaterializerTests
{
    private static readonly DateTimeOffset ExpiresAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public async Task MaterializeAsync_ShouldIssueAndStoreOwnerScopedCredential()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault();
        var plan = Plan(AuthorizationOwnerKind.Personal, "owner-alpha");

        var credential = await new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            plan,
            "schedule-alpha",
            OwnerScope.ForNyxIdNative("owner-alpha"));

        credential.ApiKeyId.Should().Be("api-key-alpha");
        credential.SecretReference.Should().BeSameAs(vault.StoredReference);
        credential.ExpiresAtUtc.Should().Be(ExpiresAt);
        credential.Owner.Should().Be(new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-alpha"));
        var issue = issuer.Issues.Should().ContainSingle().Which;
        issue.Token.Should().Be("bearer-alpha");
        issue.Plan.Should().BeSameAs(plan);
        issue.CredentialName.Should().Be("studio-schedule-schedule-alpha");
        var store = vault.Stores.Should().ContainSingle().Which;
        store.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        store.OwnerScopeKey.Should().Be("schedule:schedule-alpha");
        store.SubjectId.Should().Be("api-key-alpha");
        store.Secret.Should().Be("secret-value");
        store.ExpiresAt.Should().Be(ExpiresAt);
    }

    [Fact]
    public async Task MaterializeAsync_WhenIssuerFails_ShouldPropagateWithoutStoringSecret()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Failed("issuer-rejected"),
        };
        var vault = new RecordingSecretVault();

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            OwnerScope.ForNyxIdNative("owner-alpha"));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("issuer-rejected");
        vault.Stores.Should().BeEmpty();
        issuer.Revocations.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenVaultFails_ShouldRevokeIssuedKey()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault { StoreException = new InvalidOperationException("vault-failed") };

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            OwnerScope.ForNyxIdNative("owner-alpha"));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("vault-failed");
        issuer.Revocations.Should().ContainSingle().Which.Should().Be(("bearer-alpha", "api-key-alpha"));
    }

    [Fact]
    public async Task RevokeAsync_WhenAuthenticatedOwnerDiffers_ShouldRejectBeforeEffects()
    {
        var issuer = new RecordingIssuer();
        var vault = new RecordingSecretVault();
        var credential = Credential(new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-alpha"));

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).RevokeAsync(
            "bearer-beta",
            AuthenticatedOwner(AuthorizationOwnerKind.Personal, "owner-beta"),
            credential,
            revokeNyxId: true,
            revokeVault: true);

        await action.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("credential_owner_mismatch");
        issuer.Revocations.Should().BeEmpty();
        vault.Revocations.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_ShouldAcceptEquivalentOrganizationOwnerAndRevokeBothTracks()
    {
        var issuer = new RecordingIssuer();
        var vault = new RecordingSecretVault();
        var credential = Credential(new ScheduledInvocationAuthorizationOwner(
            "nyxid",
            "Organization",
            "org-alpha"));

        var result = await new StudioScheduledCredentialMaterializer(issuer, vault).RevokeAsync(
            "bearer-org",
            AuthenticatedOwner(AuthorizationOwnerKind.Organization, "org-alpha"),
            credential,
            revokeNyxId: true,
            revokeVault: true);

        result.Should().Be(new StudioScheduledCredentialRevocationResult(true, true, string.Empty));
        issuer.Revocations.Should().ContainSingle().Which.Should().Be(("bearer-org", "api-key-alpha"));
        vault.Revocations.Should().ContainSingle().Which.Should().BeEquivalentTo(new RevokeSecretRequest(
            "secret-ref-alpha",
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "schedule:schedule-alpha",
            "api-key-alpha",
            "team-automation-credential-revocation"));
    }

    private static ValidatedScheduledInvocationAuthorizationPlan Plan(
        AuthorizationOwnerKind ownerKind,
        string ownerSubject)
    {
        var plan = new ScheduledInvocationAuthorizationPlan
        {
            PermissionDigest = "digest-alpha",
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = "nyxid",
                OwnerKind = ownerKind,
                OwnerSubject = ownerSubject,
            },
        };
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant { UserServiceId = "service-alpha" });
        plan.NyxIdNodeGrants.Add(new NyxIdNodeGrant
        {
            UserServiceId = "service-alpha",
            NodeId = "node-alpha",
            Role = NyxIdNodeRole.Primary,
        });
        return new ValidatedScheduledInvocationAuthorizationPlan(plan);
    }

    private static AuthenticatedAuthorizationOwnerContext AuthenticatedOwner(
        AuthorizationOwnerKind ownerKind,
        string ownerSubject) =>
        new(
            new AuthorizationOwnerIdentity
            {
                Authority = "nyxid",
                OwnerKind = ownerKind,
                OwnerSubject = ownerSubject,
            },
            "nyxid",
            string.Empty,
            ownerSubject,
            "binding-alpha");

    private static StudioScheduledCredential Credential(ScheduledInvocationAuthorizationOwner owner) =>
        new(
            "api-key-alpha",
            new SecretReference
            {
                Ref = "secret-ref-alpha",
                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                OwnerScopeKey = "schedule:schedule-alpha",
                ExpiresAtUnixMs = ExpiresAt.ToUnixTimeMilliseconds(),
            },
            ExpiresAt,
            owner);

    private sealed class RecordingIssuer : IScheduledAgentApiKeyIssuer
    {
        public ScheduledAgentApiKeyIssueResult IssueResult { get; init; } =
            ScheduledAgentApiKeyIssueResult.Failed("not-configured");

        public ScheduledAgentApiKeyRevokeResult RevokeResult { get; init; } =
            ScheduledAgentApiKeyRevokeResult.Complete();

        public List<IssueRequest> Issues { get; } = [];

        public List<(string Token, string ApiKeyId)> Revocations { get; } = [];

        public Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
            string token,
            ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
            string credentialName,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Issues.Add(new IssueRequest(token, validatedPlan, credentialName));
            return Task.FromResult(IssueResult);
        }

        public Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(
            string token,
            string apiKeyId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Revocations.Add((token, apiKeyId));
            return Task.FromResult(RevokeResult);
        }
    }

    private sealed class RecordingSecretVault : ISecretVault
    {
        public SecretReference StoredReference { get; } = new()
        {
            Ref = "secret-ref-alpha",
            Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = "schedule:schedule-alpha",
            ExpiresAtUnixMs = ExpiresAt.ToUnixTimeMilliseconds(),
        };

        public Exception? StoreException { get; init; }

        public List<StoreSecretRequest> Stores { get; } = [];

        public List<RevokeSecretRequest> Revocations { get; } = [];

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stores.Add(request);
            if (StoreException != null)
                throw StoreException;
            return Task.FromResult(new StoreSecretResult(StoredReference));
        }

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Revocations.Add(request);
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }

    private sealed record IssueRequest(
        string Token,
        ValidatedScheduledInvocationAuthorizationPlan Plan,
        string CredentialName);
}
