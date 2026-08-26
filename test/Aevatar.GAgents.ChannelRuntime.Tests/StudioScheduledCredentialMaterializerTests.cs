using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;
using System.Text;

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
                ExpiresAt.ToUnixTimeMilliseconds(),
                [DurableGrant()]),
        };
        var vault = new RecordingSecretVault();
        var plan = Plan(AuthorizationOwnerKind.Personal, "owner-alpha");

        var credential = await new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            plan,
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        credential.ApiKeyId.Should().Be("api-key-alpha");
        credential.SecretReference.Should().BeSameAs(vault.StoredReference);
        credential.ExpiresAtUtc.Should().Be(ExpiresAt);
        credential.Owner.Should().Be(new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-alpha"));
        credential.DurableOperationGrants.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(DurableGrant());
        var issue = issuer.Issues.Should().ContainSingle().Which;
        issue.Token.Should().Be("bearer-alpha");
        issue.Plan.Should().BeSameAs(plan);
        issue.CredentialName.Should().Be(StudioScheduledCredentialMaterializer.BuildCredentialName(
            "schedule-alpha",
            "operation-alpha"));
        var reconciliation = issuer.Reconciliations.Should().ContainSingle().Which;
        reconciliation.Should().Be(new ReconcileRequest(
            "bearer-alpha",
            plan,
            issue.CredentialName));
        issuer.Events.Should().Equal("lookup", "issue");
        var store = vault.Stores.Should().ContainSingle().Which;
        store.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        store.OwnerScopeKey.Should().Be("schedule:schedule-alpha");
        store.SubjectId.Should().Be("api-key-alpha");
        store.Secret.Should().Be("secret-value");
        store.ExpiresAt.Should().Be(ExpiresAt);
        store.RequestedRef.Should().Be(StudioScheduledCredentialMaterializer.BuildRequestedSecretReference(
            "schedule-alpha",
            "operation-alpha"));
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
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var failure = await action.Should()
            .ThrowAsync<StudioScheduledCredentialMaterializationException>()
            .WithMessage("issuer-rejected");
        failure.Which.EffectsCleaned.Should().BeTrue();
        failure.Which.FailureCode.Should().Be("issuer-rejected");
        vault.Stores.Should().BeEmpty();
        issuer.Revocations.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenAuthorizationPlanMismatch_ShouldPropagateTypedConflict()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Failed(
                "authorization_plan_changed",
                authorizationPlanMismatchReason: ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch),
        };
        var vault = new RecordingSecretVault();

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage("authorization_plan_changed");
        conflict.Which.Code.Should().Be("authorization_plan_changed");
        conflict.Which.AuthorizationPlanMismatchReason.Should()
            .Be(ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch);
        vault.Stores.Should().BeEmpty();
        issuer.Revocations.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenAuthorizationPlanMismatchAfterIssue_ShouldCleanupAndPropagateTypedConflict()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.FailedAfterIssue(
                "api-key-alpha",
                "authorization_plan_changed",
                authorizationPlanMismatchReason: ScheduledAuthorizationPlanMismatchReason.AllowedServiceIdsMismatch),
        };
        var vault = new RecordingSecretVault();

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>()
            .WithMessage("authorization_plan_changed");
        conflict.Which.AuthorizationPlanMismatchReason.Should()
            .Be(ScheduledAuthorizationPlanMismatchReason.AllowedServiceIdsMismatch);
        issuer.Revocations.Should().ContainSingle().Which.Should().Be(("bearer-alpha", "api-key-alpha"));
        vault.Revocations.Should().ContainSingle().Which.SubjectId.Should().Be("api-key-alpha");
        vault.Stores.Should().BeEmpty();
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
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var failure = await action.Should()
            .ThrowAsync<StudioScheduledCredentialMaterializationException>()
            .WithMessage("vault-failed");
        failure.Which.EffectsCleaned.Should().BeTrue();
        vault.Revocations.Should().ContainSingle().Which.Ref.Should().Be(
            StudioScheduledCredentialMaterializer.BuildRequestedSecretReference(
                "schedule-alpha",
                "operation-alpha"));
        issuer.Revocations.Should().ContainSingle().Which.Should().Be(("bearer-alpha", "api-key-alpha"));
    }

    [Fact]
    public async Task MaterializeAsync_WhenReconciliationFails_ShouldNotIssueOrStore()
    {
        var issuer = new RecordingIssuer
        {
            LookupResult = ScheduledAgentApiKeyLookupResult.Pending(
                503,
                "list-failed",
                UserAgentApiKeyRevocationFailureKind.Transient),
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault();

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scheduled_credential_reconciliation_failed");
        issuer.Events.Should().Equal("lookup");
        issuer.Issues.Should().BeEmpty();
        issuer.Revocations.Should().BeEmpty();
        vault.Stores.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenVaultAndCleanupFail_ShouldSurfaceCleanupFailure()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
            RevokeResult = ScheduledAgentApiKeyRevokeResult.Pending(
                503,
                "revoke-failed",
                UserAgentApiKeyRevocationFailureKind.Transient),
        };
        var vault = new RecordingSecretVault { StoreException = new InvalidOperationException("vault-failed") };

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var exception = await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scheduled_credential_cleanup_failed");
        exception.Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().Contain(error => error.Message == "vault-failed");
        issuer.Revocations.Should().ContainSingle().Which.Should().Be(("bearer-alpha", "api-key-alpha"));
    }

    [Fact]
    public async Task MaterializeAsync_WhenCommittedOwnerDiffersFromPlan_ShouldRejectBeforeEffects()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault();
        var locator = EffectLocator("schedule-alpha", "operation-alpha") with
        {
            CredentialOwner = new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-beta"),
        };

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            locator,
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scheduled_credential_effect_locator_mismatch");
        issuer.Events.Should().BeEmpty();
        vault.Stores.Should().BeEmpty();
        vault.Revocations.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_AfterRestart_ShouldRecoverCommittedLocatorEffectsBeforeReissuing()
    {
        var issuer = new RecordingIssuer
        {
            LookupResult = ScheduledAgentApiKeyLookupResult.Complete(["api-key-orphan"]),
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-replacement",
                "replacement-secret",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault();
        var locator = EffectLocator("schedule-alpha", "operation-alpha");

        var credential = await new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            locator,
            StudioScheduledCredentialMaterializationMode.Recovery,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        credential.ApiKeyId.Should().Be("api-key-replacement");
        issuer.Events.Should().Equal("lookup", "revoke:api-key-orphan", "issue");
        vault.Revocations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RevokeSecretRequest(
                locator.RequestedSecretReference,
                locator.SecretPurpose,
                locator.SecretOwnerScopeKey,
                "api-key-orphan",
                "scheduled-credential-recovery"));
        vault.Stores.Should().ContainSingle().Which.RequestedRef.Should().Be(locator.RequestedSecretReference);
    }

    [Fact]
    public async Task MaterializeAsync_OnRecoveryAttemptWithoutExactNameEvidence_ShouldNotRemint()
    {
        var issuer = new RecordingIssuer
        {
            LookupResult = ScheduledAgentApiKeyLookupResult.Complete([]),
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-unproven",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault();

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Recovery,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var failure = await action.Should()
            .ThrowAsync<StudioScheduledCredentialMaterializationException>()
            .WithMessage("scheduled_credential_recovery_evidence_missing");
        failure.Which.EffectsCleaned.Should().BeFalse();
        failure.Which.RecoveryBlocked.Should().BeTrue();
        failure.Which.FailureCode.Should().Be("scheduled_credential_recovery_evidence_missing");
        issuer.Events.Should().Equal("lookup");
        issuer.Issues.Should().BeEmpty();
        vault.Stores.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenVaultPutCommitsButResponseFails_ShouldCleanBothEffects()
    {
        var issuer = new RecordingIssuer
        {
            IssueResult = ScheduledAgentApiKeyIssueResult.Succeeded(
                "api-key-alpha",
                "secret-value",
                ExpiresAt.ToUnixTimeMilliseconds()),
        };
        var vault = new RecordingSecretVault
        {
            StoreThenThrowException = new InvalidOperationException("vault-put-ambiguous"),
        };

        var action = () => new StudioScheduledCredentialMaterializer(issuer, vault).MaterializeAsync(
            "bearer-alpha",
            Plan(AuthorizationOwnerKind.Personal, "owner-alpha"),
            "schedule-alpha",
            "operation-alpha",
            EffectLocator("schedule-alpha", "operation-alpha"),
            StudioScheduledCredentialMaterializationMode.Initial,
            OwnerScope.ForNyxIdNative("owner-alpha"));

        var failure = await action.Should()
            .ThrowAsync<StudioScheduledCredentialMaterializationException>()
            .WithMessage("vault-put-ambiguous");
        failure.Which.EffectsCleaned.Should().BeTrue();
        vault.StoreCommitted.Should().BeTrue();
        vault.StoredReference.Should().BeNull();
        vault.Revocations.Should().ContainSingle().Which.SubjectId.Should().Be("api-key-alpha");
        issuer.Revocations.Should().ContainSingle().Which.ApiKeyId.Should().Be("api-key-alpha");
    }

    [Fact]
    public void BuildCredentialName_ShouldBeDeterministicBoundedAndOperationScoped()
    {
        var scheduleId = new string('\u4e00', 500) + "schedule-alpha";
        var operationId = new string('\u4e8c', 500) + "operation-alpha";

        var first = StudioScheduledCredentialMaterializer.BuildCredentialName(scheduleId, operationId);
        var replay = StudioScheduledCredentialMaterializer.BuildCredentialName(scheduleId, operationId);
        var nextOperation = StudioScheduledCredentialMaterializer.BuildCredentialName(
            scheduleId,
            operationId + "-next");

        first.Should().Be(replay);
        first.Should().NotBe(nextOperation);
        first.Should().StartWith("studio-schedule-");
        first.Should().MatchRegex("^[a-z0-9-]+$");
        Encoding.UTF8.GetByteCount(first).Should().BeLessThanOrEqualTo(200);
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
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "service-alpha",
            NodeGrantRequirement = AuthorizationGrantRequirement.Required,
            NodeIds = { "node-alpha" },
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

    private static NyxIdDurableOperationGrantRef DurableGrant() => new()
    {
        GrantId = "grant-executions",
        ApiKeyId = "api-key-alpha",
        UserServiceId = "service-alpha",
        EndpointId = "endpoint-executions",
        HttpMethod = NyxIdDurableOperationHttpMethod.Post,
        NormalizedPathTemplate = "/executions",
        ContractDigest =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        ValidFromUnixMs = ExpiresAt.AddDays(-2).ToUnixTimeMilliseconds(),
        ExpiresAtUnixMs = ExpiresAt.AddDays(-1).ToUnixTimeMilliseconds(),
        ReplayPolicy = NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey,
    };

    private static ScheduledCredentialEffectLocator EffectLocator(
        string scheduleId,
        string operationId) =>
        new(
            StudioScheduledCredentialMaterializer.BuildCredentialName(scheduleId, operationId),
            StudioScheduledCredentialMaterializer.BuildRequestedSecretReference(scheduleId, operationId),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            $"schedule:{scheduleId}",
            new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-alpha"));

    private sealed class RecordingIssuer : IScheduledAgentApiKeyIssuer
    {
        public ScheduledAgentApiKeyLookupResult LookupResult { get; init; } =
            ScheduledAgentApiKeyLookupResult.Complete([]);

        public ScheduledAgentApiKeyIssueResult IssueResult { get; init; } =
            ScheduledAgentApiKeyIssueResult.Failed("not-configured");

        public ScheduledAgentApiKeyRevokeResult RevokeResult { get; init; } =
            ScheduledAgentApiKeyRevokeResult.Complete();

        public List<IssueRequest> Issues { get; } = [];

        public List<ReconcileRequest> Reconciliations { get; } = [];

        public List<(string Token, string ApiKeyId)> Revocations { get; } = [];

        public List<string> Events { get; } = [];

        public Task<ScheduledAgentApiKeyLookupResult> FindActiveKeysByNameAsync(
            string token,
            ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
            string credentialName,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add("lookup");
            Reconciliations.Add(new ReconcileRequest(token, validatedPlan, credentialName));
            return Task.FromResult(LookupResult);
        }

        public Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
            string token,
            ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
            string credentialName,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add("issue");
            Issues.Add(new IssueRequest(token, validatedPlan, credentialName));
            return Task.FromResult(IssueResult);
        }

        public Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(
            string token,
            string apiKeyId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add($"revoke:{apiKeyId}");
            Revocations.Add((token, apiKeyId));
            return Task.FromResult(RevokeResult);
        }
    }

    private sealed class RecordingSecretVault : ISecretVault
    {
        public SecretReference? StoredReference { get; private set; }

        public bool StoreCommitted { get; private set; }

        public Exception? StoreException { get; init; }

        public Exception? StoreThenThrowException { get; init; }

        public List<StoreSecretRequest> Stores { get; } = [];

        public List<RevokeSecretRequest> Revocations { get; } = [];

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stores.Add(request);
            if (StoreException != null)
                throw StoreException;
            StoredReference = new SecretReference
            {
                Ref = request.RequestedRef,
                Purpose = request.Purpose,
                OwnerScopeKey = request.OwnerScopeKey,
                ExpiresAtUnixMs = request.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0,
            };
            StoreCommitted = true;
            if (StoreThenThrowException != null)
                throw StoreThenThrowException;
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
            if (string.Equals(StoredReference?.Ref, request.Ref, StringComparison.Ordinal))
                StoredReference = null;
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }

    private sealed record IssueRequest(
        string Token,
        ValidatedScheduledInvocationAuthorizationPlan Plan,
        string CredentialName);

    private sealed record ReconcileRequest(
        string Token,
        ValidatedScheduledInvocationAuthorizationPlan Plan,
        string CredentialName);
}
