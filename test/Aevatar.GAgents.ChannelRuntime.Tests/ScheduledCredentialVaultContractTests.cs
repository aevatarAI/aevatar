using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledCredentialVaultContractTests
{
    [Fact]
    public async Task PutAsync_WithRequestedReference_IsIdempotentForExactCreate()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        var request = new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "opaque-secret",
            "test",
            RequestedRef: "sec_requested_a");

        var first = await vault.PutAsync(request);
        var second = await vault.PutAsync(request);

        first.Reference.Ref.Should().Be("sec_requested_a");
        second.Reference.Should().BeEquivalentTo(first.Reference);
    }

    [Fact]
    public async Task PutAsync_WithRequestedReference_RejectsAliasConflict()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "opaque-secret",
            "test",
            RequestedRef: "sec_requested_a"));

        Func<Task> act = () => vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-b",
            "different-secret",
            "test",
            RequestedRef: "sec_requested_a"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task InMemoryVault_RevokeMissingReference_ReturnsSuccess()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();

        var revoked = await vault.RevokeAsync(new RevokeSecretRequest(
            "sec-missing",
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "test missing revoke"));

        revoked.Revoked.Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryVault_RevokeSameReferenceTwice_ReturnsSuccessBothTimes()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "opaque-secret",
            "test"));
        var request = new RevokeSecretRequest(
            stored.Reference.Ref,
            stored.Reference.Purpose,
            stored.Reference.OwnerScopeKey,
            "key-a",
            "test repeated revoke");

        var first = await vault.RevokeAsync(request);
        var second = await vault.RevokeAsync(request);

        first.Revoked.Should().BeTrue();
        second.Revoked.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_WhenIssueFails_DoesNotWriteVaultOrRevocationIntent()
    {
        var vault = Substitute.For<ISecretVault>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ValidatedScheduledInvocationAuthorizationPlan>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Failed("issue_failed"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        var result = await lifecycle.ProvisionAsync(
            "token",
            AuthorizationPlan(),
            "scheduled-agent-a",
            "agent-a",
            Owner(),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        result.Success.Should().BeFalse();
        await vault.DidNotReceive().PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>());
        await commandPort.DidNotReceive().RequestCredentialRevocationAsync(
            Arg.Any<ScheduledAgentCredentialRevocationIntent>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenIssueFailsAfterMint_SubmitsNyxOnlyIntentWithoutFakeReference()
    {
        var vault = Substitute.For<ISecretVault>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ValidatedScheduledInvocationAuthorizationPlan>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.FailedAfterIssue("key-a", "preflight_failed"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        var result = await lifecycle.ProvisionAsync(
            "token",
            AuthorizationPlan(),
            "scheduled-agent-a",
            "agent-a",
            Owner(),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        result.Success.Should().BeFalse();
        await vault.DidNotReceive().PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>());
        await commandPort.Received(1).RequestCredentialRevocationAsync(
            Arg.Is<ScheduledAgentCredentialRevocationIntent>(intent =>
                intent.ApiKeyId == "key-a" &&
                intent.NyxApiKeyReference == null &&
                intent.OwnerScope.MatchesStrictly(Owner()) &&
                intent.VaultRevocationDescriptor.ReferenceAvailability ==
                    ScheduledCredentialVaultReferenceAvailability.NotApplicable),
            Arg.Any<CancellationToken>(),
            "token");
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultWriteFails_SubmitsDualTrackRevocationIntent()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoreSecretResult>>(_ => throw new InvalidOperationException("vault unavailable"));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ValidatedScheduledInvocationAuthorizationPlan>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Succeeded("key-a", "raw-secret"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        Func<Task> act = () => lifecycle.ProvisionAsync(
            "token",
            AuthorizationPlan(),
            "scheduled-agent-a",
            "agent-a",
            Owner(),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        await act.Should().ThrowAsync<InvalidOperationException>();
        await commandPort.Received(1).RequestCredentialRevocationAsync(
            Arg.Is<ScheduledAgentCredentialRevocationIntent>(intent =>
                intent.AgentId == "agent-a" &&
                intent.ApiKeyId == "key-a" &&
                intent.NyxApiKeyReference == null &&
                intent.OwnerScope.MatchesStrictly(Owner()) &&
                intent.VaultRevocationDescriptor.Ref.StartsWith("sec_", StringComparison.Ordinal) &&
                intent.VaultRevocationDescriptor.Purpose ==
                    CredentialSecretPurposes.ScheduledInvocationAgentKey &&
                intent.VaultRevocationDescriptor.OwnerScopeKey == "owner-a" &&
                intent.VaultRevocationDescriptor.SubjectId == "key-a" &&
                intent.VaultRevocationDescriptor.ReferenceAvailability ==
                    ScheduledCredentialVaultReferenceAvailability.RequestedNotConfirmed),
            Arg.Any<CancellationToken>(),
            "token");
    }

    [Fact]
    public async Task ProvisionAsync_WhenSuccessful_ReturnsDurableRequestedReference()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ValidatedScheduledInvocationAuthorizationPlan>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Succeeded("key-a", "raw-secret"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        var result = await lifecycle.ProvisionAsync(
            "token",
            AuthorizationPlan(),
            "scheduled-agent-a",
            "agent-a",
            Owner(),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        result.Success.Should().BeTrue();
        result.SecretReference!.Ref.Should().StartWith("sec_");
        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            result.SecretReference.Ref,
            result.SecretReference.Purpose,
            result.SecretReference.OwnerScopeKey,
            "key-a",
            "test"));
        resolved.Secret.Should().Be("raw-secret");
    }

    [Fact]
    public void RevocationIntent_DoesNotExposeAuthoritativeTrackOrAuditState()
    {
        typeof(ScheduledAgentCredentialRevocationIntent).GetProperties()
            .Select(static property => property.Name)
            .Should().NotContain(
            [
                "NyxIdTrack",
                "VaultTrack",
                "AttemptCount",
                "RequestedAt",
                "RepairReason",
                "RequestedBySubjectId",
            ]);
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenBothTracksSucceed_RecordsTypedCompletionForEachTrack()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.RevokeAsync("token", "key-a", Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyRevokeResult.Complete(204));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        await lifecycle.ExecutePendingAsync("token", PendingRevocation());

        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId &&
                command.Completed &&
                command.HttpStatus == 204 &&
                command.FailureKind == UserAgentApiKeyRevocationFailureKind.None),
            Arg.Any<CancellationToken>());
        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault &&
                command.Completed &&
                command.FailureKind == UserAgentApiKeyRevocationFailureKind.None),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenNyxIdRevocationThrows_RecordsTransientFailure()
    {
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.RevokeAsync("token", "key-a", Arg.Any<CancellationToken>())
            .Returns<Task<ScheduledAgentApiKeyRevokeResult>>(
                _ => throw new InvalidOperationException("nyx unavailable"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            Substitute.For<ISecretVault>(),
            commandPort,
            issuer);
        var pending = PendingRevocation();
        pending.VaultTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;

        await lifecycle.ExecutePendingAsync("token", pending);

        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId &&
                !command.Completed &&
                command.Error == "nyx unavailable" &&
                command.FailureKind == UserAgentApiKeyRevocationFailureKind.Transient &&
                command.SecretReferenceRef == "sec-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenNyxIdRevocationIsCanceled_PropagatesCancellation()
    {
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.RevokeAsync("token", "key-a", Arg.Any<CancellationToken>())
            .Returns<Task<ScheduledAgentApiKeyRevokeResult>>(
                _ => throw new OperationCanceledException("canceled"));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            Substitute.For<ISecretVault>(),
            commandPort,
            issuer);
        var pending = PendingRevocation();
        pending.VaultTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;

        var execute = () => lifecycle.ExecutePendingAsync("token", pending);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        await commandPort.DidNotReceive().RecordApiKeyRevocationAttemptAsync(
            Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenVaultRevocationIsCanceled_PropagatesWithoutRecordingFailure()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<RevokeSecretResult>>(
                _ => throw new OperationCanceledException("canceled"));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            vault,
            commandPort,
            Substitute.For<IScheduledAgentApiKeyIssuer>());
        var pending = PendingRevocation();
        pending.NyxIdTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;

        var execute = () => lifecycle.ExecutePendingAsync("token", pending);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        await commandPort.DidNotReceive().RecordApiKeyRevocationAttemptAsync(
            Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenVaultReferenceIsNotActive_RecordsProviderFailure()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(false));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            vault,
            commandPort,
            Substitute.For<IScheduledAgentApiKeyIssuer>());
        var pending = PendingRevocation();
        pending.NyxIdTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;

        await lifecycle.ExecutePendingAsync("token", pending);

        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault &&
                !command.Completed &&
                command.Error == "secret_reference_not_active" &&
                command.FailureKind == UserAgentApiKeyRevocationFailureKind.ProviderError),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenVaultThrows_RecordsTransientFailure()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<RevokeSecretResult>>(_ => throw new InvalidOperationException("vault unavailable"));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            vault,
            commandPort,
            Substitute.For<IScheduledAgentApiKeyIssuer>());
        var pending = PendingRevocation();
        pending.NyxIdTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;

        await lifecycle.ExecutePendingAsync("token", pending);

        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault &&
                !command.Completed &&
                command.Error == "vault unavailable" &&
                command.FailureKind == UserAgentApiKeyRevocationFailureKind.Transient),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePendingAsync_WithUnconfirmedRequestedReference_RevokesByTypedDescriptor()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var lifecycle = new ScheduledAgentCredentialLifecycle(
            vault,
            commandPort,
            Substitute.For<IScheduledAgentApiKeyIssuer>());
        var pending = PendingRevocation();
        pending.NyxApiKeyReference = null;
        pending.NyxIdTrack.Status = ScheduledCredentialRevocationTrackStatus.Completed;
        pending.VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
        {
            Ref = "sec-requested",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "owner-requested",
            SubjectId = "key-a",
            ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.RequestedNotConfirmed,
        };

        await lifecycle.ExecutePendingAsync("token", pending);

        await vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == "sec-requested" &&
                request.Purpose == CredentialSecretPurposes.ScheduledNyxApiKey &&
                request.OwnerScopeKey == "owner-requested" &&
                request.SubjectId == "key-a"),
            Arg.Any<CancellationToken>());
        await commandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
            Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                command.Track == UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault &&
                command.SecretReferenceRef == "sec-requested" &&
                command.Completed),
            Arg.Any<CancellationToken>());
    }

    private static UserAgentApiKeyRevocation PendingRevocation() => new()
    {
        AgentId = "agent-a",
        ApiKeyId = "key-a",
        SecretSubjectId = "key-a",
        NyxApiKeyReference = new SecretReference
        {
            Ref = "sec-a",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "owner-a",
            Version = 1,
            Fingerprint = "sha256:test",
        },
        NyxIdTrack = new ScheduledCredentialRevocationTrack
        {
            Status = ScheduledCredentialRevocationTrackStatus.Pending,
        },
        VaultTrack = new ScheduledCredentialRevocationTrack
        {
            Status = ScheduledCredentialRevocationTrackStatus.Pending,
        },
    };

    private static OwnerScope Owner() => OwnerScope.ForNyxIdNative("user-a");

    private static ValidatedScheduledInvocationAuthorizationPlan AuthorizationPlan() =>
        new(new ScheduledInvocationAuthorizationPlan
        {
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "user-a",
            },
            PermissionDigest = "permission-digest-a",
        });
}
