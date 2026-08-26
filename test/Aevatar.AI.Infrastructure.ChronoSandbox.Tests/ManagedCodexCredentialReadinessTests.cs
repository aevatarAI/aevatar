using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexCredentialReadinessTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-21T12:00:00Z");
    private const string RawKey = "nyx_k_readiness-secret-must-remain-secret";

    private readonly IManagedCodexNyxIdCredentialPort _nyxId =
        Substitute.For<IManagedCodexNyxIdCredentialPort>();
    private readonly ISecretVault _vault = Substitute.For<ISecretVault>();
    private readonly IManagedCodexCredentialQueryPort _query =
        Substitute.For<IManagedCodexCredentialQueryPort>();
    private readonly IManagedCodexCredentialCommandPort _commands =
        Substitute.For<IManagedCodexCredentialCommandPort>();
    private readonly IManagedCodexCredentialMutationLease _lease =
        Substitute.For<IManagedCodexCredentialMutationLease>();
    private readonly RecordingManagedCodexReadinessObservationPort _observation = new();
    private readonly List<ManagedCodexCredentialCleanup> _committedPendingCleanups = [];
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ManagedCodexCredentialLifecycle _lifecycle;

    public ManagedCodexCredentialReadinessTests()
    {
        _query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns((ManagedCodexCredentialSnapshot?)null);
        _nyxId.GetCurrentUserIdAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns("user-a");
        _nyxId.ListUserServicesAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(UserServices("us-sandbox", "us-llm"));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-new"));
        _nyxId.UpdateApiKeyPolicyAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ManagedCodexNyxIdApiKeyPolicyUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _nyxId.RevokeApiKeyAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ResolveSecretRequest>();
                return new ResolveSecretResult(
                    Reference(request.Ref, request.OwnerScopeKey, Now.AddDays(30)),
                    RawKey);
            });
        _vault.PutAsync(
                Arg.Any<StoreSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<StoreSecretRequest>();
                return new StoreSecretResult(Reference(
                    request.RequestedRef!,
                    request.OwnerScopeKey,
                    request.ExpiresAt!.Value));
            });
        _vault.RevokeAsync(
                Arg.Any<RevokeSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));

        _commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                CaptureCommittedCleanups(
                    call.ArgAt<IReadOnlyList<ManagedCodexCredentialCleanup>>(1));
                return Dispatch();
            });
        _commands.CommitRotatedAsync(
                Arg.Any<string>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                CaptureCommittedCleanups(
                    [call.ArgAt<ManagedCodexCredentialCleanup>(2)],
                    preferNyxIdTrack: true);
                CaptureCommittedCleanups(
                    call.ArgAt<IReadOnlyList<ManagedCodexCredentialCleanup>>(3));
                return Dispatch();
            });
        _commands.CommitPolicyReconciledAsync(
                Arg.Any<string>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                CaptureCommittedCleanups(
                    call.ArgAt<IReadOnlyList<ManagedCodexCredentialCleanup>>(2));
                return Dispatch();
            });
        _commands.ConfirmReadinessAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<ManagedCodexCredentialReadinessEvidence>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Dispatch());
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Dispatch());
        _commands.CompleteCleanupTrackAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ManagedCodexCredentialCleanupTrack>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Dispatch());

        var leaseHandle = Substitute.For<IManagedCodexCredentialMutationLeaseHandle>();
        _lease.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(leaseHandle);
        _lifecycle = CreateLifecycle();
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenProjectionIsReady_ReturnsWithoutBearerOrMutation()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-a");
        await _nyxId.DidNotReceiveWithAnyArgs()
            .GetCurrentUserIdAsync(default!, default);
        await _vault.DidNotReceiveWithAnyArgs()
            .ResolveAsync(default!, default);
        await _lease.DidNotReceiveWithAnyArgs()
            .TryAcquireAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenNativeOwnerCarriesTenant_FailsBeforeDependencies()
    {
        var owner = Owner("user-a");
        owner.Tenant = "unattested-tenant";

        var act = () => _lifecycle.EnsureReadyAsync(
            owner,
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
                .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("nyxid_identity_mismatch");
        await _query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _lease.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenProjectedVaultReferenceIsEmpty_DoesNotTreatItAsReady()
    {
        var invalid = ReadyDescriptor();
        invalid.SecretReference.Ref = string.Empty;
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(invalid, stateVersion: 3));

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_user_authorization_unavailable");
        await _lease.Received(1).TryAcquireAsync(
            "managed-codex-credential:nyxid::user-a",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenLeaseAcquisitionConsumesPrimaryBudget_DoesNotStartRemoteWork()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var leaseHandle =
            Substitute.For<IManagedCodexCredentialMutationLeaseHandle>();
        _lease.TryAcquireAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _time.Advance(TimeSpan.FromSeconds(60));
                return leaseHandle;
            });
        _nyxId.GetCurrentUserIdAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return "user-a";
            });
        PublishConfirmedCredential(stateVersion: 1);
        var lifecycle = CreateLifecycle(options: options);

        var act = () => lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_commit_timeout");
        await leaseHandle.Received(1).DisposeAsync();
        await _nyxId.DidNotReceiveWithAnyArgs()
            .ListApiKeysAsync(default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadinessCommitsAfterBinding_ReReadsBeforeTakingLease()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(
                (ManagedCodexCredentialSnapshot?)null,
                Snapshot(ReadyDescriptor("key-after-bind"), stateVersion: 2));

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-after-bind");
        await _query.Received(2).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _lease.DidNotReceiveWithAnyArgs()
            .TryAcquireAsync(default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .GetCurrentUserIdAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadinessCommitsBeforeLeaseOwnerMutates_ReReadsWithoutBearer()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(
                (ManagedCodexCredentialSnapshot?)null,
                (ManagedCodexCredentialSnapshot?)null,
                Snapshot(ReadyDescriptor("key-after-lease"), stateVersion: 2));

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-after-lease");
        await _query.Received(3).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _lease.Received(1).TryAcquireAsync(
            "managed-codex-credential:nyxid::user-a",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceiveWithAnyArgs()
            .GetCurrentUserIdAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenMissing_ProvisionsWaitsForCommitAndReturnsObservedDescriptor()
    {
        PublishConfirmedCredential(stateVersion: 1);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-new");
        ready.Should().BeEquivalentTo(_observation.LastPublished!.Credential);
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-new"),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenLeaseIsBusy_WaitsForOtherInvocationCommit()
    {
        _lease.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IManagedCodexCredentialMutationLeaseHandle?)null);
        _observation.Publish(
            Snapshot(ReadyDescriptor("key-from-other-call"), stateVersion: 2));

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-from-other-call");
        await _nyxId.DidNotReceiveWithAnyArgs()
            .GetCurrentUserIdAsync(default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenLeaseOwnerHasNoBearer_FailsBeforeExternalReads()
    {
        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_user_authorization_unavailable");
        await _query.Received(3).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _lease.Received(1).TryAcquireAsync(
            "managed-codex-credential:nyxid::user-a",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceiveWithAnyArgs()
            .GetCurrentUserIdAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenObservationEndsWithoutReadySnapshot_FailsWithCommitTimeout()
    {
        _observation.Complete();

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_commit_timeout");
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenLegacyPolicyIsSingleService_UpdatesAndObservesReadyState()
    {
        var legacy = ReadyDescriptor();
        legacy.ChronoLlmUserServiceId = string.Empty;
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(legacy, stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox"])),
                Keys(Key("key-a", ["us-sandbox", "us-llm"])));
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                    descriptor.ApiKeyId == "key-a"),
                ManagedCodexCredentialReadinessEvidence.RemoteValidated,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _observation.Publish(
                    Snapshot(
                        ReadyDescriptor(),
                        stateVersion: 4,
                        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed));
                _observation.Publish(
                    Snapshot(
                        ReadyDescriptor(),
                        stateVersion: 5,
                        ManagedCodexCredentialReadinessEvidence.RemoteValidated));
                return Admission();
            });

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ChronoLlmUserServiceId.Should().Be("us-llm");
        await _nyxId.Received(1).UpdateApiKeyPolicyAsync(
            "user-bearer",
            "key-a",
            Arg.Is<ManagedCodexNyxIdApiKeyPolicyUpdateRequest>(request =>
                request.AllowedServiceIds.Count == 2 &&
                request.AllowedServiceIds.Contains("us-sandbox") &&
                request.AllowedServiceIds.Contains("us-llm")),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CommitPolicyReconciledAsync(
            "key-a",
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCredentialExpired_CreatesFreshCredential()
    {
        var expired = ReadyDescriptor();
        expired.ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-1));
        expired.SecretReference.ExpiresAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds();
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(expired, stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        PublishConfirmedCredential(stateVersion: 4);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-fresh");
        await _nyxId.Received(1).CreateApiKeyAsync(
            "user-bearer",
            Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CommitRotatedAsync(
            "key-a",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-a" &&
                cleanup.SecretRef == expired.SecretReference.Ref &&
                cleanup.NyxIdPending &&
                cleanup.VaultPending),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForcedValidationFindsMissingVaultSecret_ReplacesKey()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        PublishConfirmedCredential(stateVersion: 4);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-replacement");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-a",
            Arg.Any<CancellationToken>());
        await _nyxId.Received(1).CreateApiKeyAsync(
            "user-bearer",
            Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SecretResolutionFailureReason.Unauthorized)]
    [InlineData(SecretResolutionFailureReason.AuthenticationFailed)]
    [InlineData(SecretResolutionFailureReason.KeyringMismatch)]
    [InlineData(SecretResolutionFailureReason.UnsupportedAlgorithm)]
    public async Task EnsureReadyAsync_WhenRemoteAdoptionVaultIsUnavailable_DoesNotReplaceKey(
        SecretResolutionFailureReason failureReason)
    {
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(Key("key-recover", ["us-sandbox", "us-llm"])));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(null, null, failureReason));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ManagedCodexNyxIdIssuedApiKey>>(_ =>
                throw new InvalidOperationException(
                    "replacement must not run when Vault authority is unavailable"));

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        var exception = (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_vault_unavailable");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-recover",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .UpdateApiKeyPolicyAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenSameKeyVaultReferenceDrifts_ReplacesAndReturnsCommittedCredential()
    {
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        projected.Credential.SecretReference.Ref = DeterministicSecretRef(
            projected.Credential.Owner,
            projected.Credential.ApiKeyId);
        var validated = projected.Credential.Clone();
        validated.SecretReference.Version++;
        validated.SecretReference.Fingerprint = "newer-fingerprint";
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                validated.SecretReference.Clone(),
                RawKey));
        PublishConfirmedCredential(stateVersion: 4);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-replacement");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            projected.Credential.ApiKeyId,
            Arg.Any<CancellationToken>());
        await _vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == projected.Credential.SecretReference.Ref),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenRotationIsAdmitted_RetiresPreviousVaultOnlyAfterMatchingCommit()
    {
        var confirmationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        projected.Credential.SecretReference.Ref = DeterministicSecretRef(
            projected.Credential.Owner,
            projected.Credential.ApiKeyId);
        ManagedCodexCredentialDescriptor? replacement = null;
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                ManagedCodexCredentialReadinessEvidence.RemoteValidated,
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                replacement = call.Arg<ManagedCodexCredentialDescriptor>().Clone();
                confirmationEntered.TrySetResult();
                await releaseConfirmation.Task;
                return Admission();
            });

        var readiness = _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await confirmationEntered.Task;

        await _vault.DidNotReceive().RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == projected.Credential.SecretReference.Ref),
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            projected.Credential.ApiKeyId,
            Arg.Any<CancellationToken>());
        releaseConfirmation.TrySetResult();
        var committed = Snapshot(
            replacement!,
            stateVersion: 4,
            ManagedCodexCredentialReadinessEvidence.RemoteValidated);
        committed.PendingRevocations.Add(
            _committedPendingCleanups.Select(static cleanup => cleanup.Clone()));
        _observation.Publish(committed);

        var ready = await readiness;

        ready.Should().BeEquivalentTo(replacement);
        await _vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == projected.Credential.SecretReference.Ref),
            Arg.Any<CancellationToken>());
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            projected.Credential.ApiKeyId,
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            projected.Credential.ApiKeyId,
            projected.Credential.SecretReference.Ref,
            ManagedCodexCredentialCleanupTrack.NyxId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            projected.Credential.ApiKeyId,
            projected.Credential.SecretReference.Ref,
            ManagedCodexCredentialCleanupTrack.Vault,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenPostCommitCleanupTimesOut_ReturnsCommittedCredential()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var lifecycle = CreateLifecycle(options: options);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverDeleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var testTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-a",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cleanupEntered.TrySetResult();
                return neverDeleted.Task.WaitAsync(call.Arg<CancellationToken>());
            });
        PublishConfirmedCredential(stateVersion: 4);

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await cleanupEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(100));

        var ready = await readiness.WaitAsync(testTimeout.Token);

        ready.ApiKeyId.Should().Be("key-replacement");
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenPostCommitCleanupCompletionIsRejected_ReturnsCommittedCredential()
    {
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _commands.CompleteCleanupTrackAsync(
                Owner("user-a"),
                "key-a",
                Arg.Any<string>(),
                Arg.Any<ManagedCodexCredentialCleanupTrack>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new DispatchAdmission(
                false,
                "command-rejected",
                Now,
                "managed-codex-credential:nyxid::user-a",
                "command-rejected"));
        PublishConfirmedCredential(stateVersion: 4);
        var deterministicSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-a");

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-replacement");
        await _commands.Received(3).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-a",
            Arg.Any<string>(),
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(2).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-a",
            "sec-key-a",
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-a",
            deterministicSecretRef,
            ManagedCodexCredentialCleanupTrack.Vault,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-a",
            deterministicSecretRef,
            ManagedCodexCredentialCleanupTrack.NyxId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReplacementQueuesAStaleReadySnapshot_WaitsForExpectedCredential()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-replacement"));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-a",
                Arg.Any<CancellationToken>())
            .Returns(false);
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                    cleanup.ApiKeyId == "key-a"),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _observation.Publish(
                    Snapshot(ReadyDescriptor("key-a"), stateVersion: 4));
                return Admission();
            });
        PublishConfirmedCredential(stateVersion: 5);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-replacement");
        ready.Should().BeEquivalentTo(_observation.LastPublished!.Credential);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenObservedActiveKeyHasNoStableId_FailsBeforeMutation()
    {
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 4);
        projected.PendingRevocations.Add(Cleanup("key-pending"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(Key("   ", ["us-sandbox", "us-llm"])));
        _observation.PublishAfterDispatch(Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed));

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_api_key_issue_invalid");
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .RotateApiKeyAsync(default!, default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .UpdateApiKeyPolicyAsync(default!, default!, default!, default);
        await _nyxId.DidNotReceiveWithAnyArgs()
            .RevokeApiKeyAsync(default!, default!, default);
        await _vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CommitProvisionedAsync(default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CommitRotatedAsync(default!, default!, default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CommitPolicyReconciledAsync(default!, default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .ConfirmReadinessAsync(default!, default!, default, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .QueueCleanupAsync(default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CompleteCleanupTrackAsync(
                default!,
                default!,
                default!,
                default,
                default,
                default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReplacementRelistContainsMalformedManagedKey_CompensatesExactIssuedKeyBeforeVaultOrActorMutation()
    {
        var issued = IssuedKey("key-new");
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(
                    issued.Key,
                    Key("   ", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(issued);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-new",
                Arg.Any<CancellationToken>())
            .Returns(true);
        PublishConfirmedCredential(stateVersion: 1);

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_api_key_issue_invalid");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-new",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "   ",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CommitProvisionedAsync(default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .CommitRotatedAsync(default!, default!, default!, default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .ConfirmReadinessAsync(default!, default!, default, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .QueueCleanupAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenMalformedKeyAppearsBeforePostCommitCleanup_SkipsCleanup()
    {
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-orphan", ["wrong-service"])),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])),
                Keys(Key("   ", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-new"));
        PublishConfirmedCredential(stateVersion: 1);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-new");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            Arg.Any<string>(),
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("task-canceled")]
    public async Task EnsureReadyAsync_WhenPostCommitCleanupPreflightHasKnownFailure_ReturnsCommittedCredential(
        string failureKind)
    {
        ArrangeReplacementWithPostCommitCleanupFailure(failureKind);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-new");
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-new"),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            Arg.Any<string>(),
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenPostCommitCleanupPreflightHasProgrammingFault_Propagates()
    {
        ArrangeReplacementWithPostCommitCleanupFailure("programming");

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Cleanup invariant failed.");
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-new"),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReservedKeysAreAmbiguous_RevokesAllAndCreatesOne()
    {
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(
                    Key("key-orphan-a", ["us-sandbox", "us-llm"]),
                    Key("key-orphan-b", ["us-sandbox", "us-llm"])),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        PublishConfirmedCredential(stateVersion: 1);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-fresh");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan-a",
            Arg.Any<CancellationToken>());
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan-b",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenOrphanNyxIdAndVaultCleanupSucceed_DoesNotQueuePendingCleanup()
    {
        ArrangeSingleOrphanReplacement(
            nyxIdDeleted: true,
            vaultRevoked: true);
        var expectedSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-orphan");

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-fresh");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan",
            Arg.Any<CancellationToken>());
        await _vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == expectedSecretRef &&
                request.OwnerScopeKey ==
                "managed-codex-credential:nyxid::user-a"),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-orphan"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenOrphanNyxIdSucceedsAndVaultFails_KeepsVaultOnlyActorCleanup()
    {
        ArrangeSingleOrphanReplacement(
            nyxIdDeleted: true,
            vaultRevoked: false);
        var expectedSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-orphan");

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-fresh");
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 1 &&
                cleanups[0].ApiKeyId == "key-orphan" &&
                cleanups[0].SecretRef == expectedSecretRef &&
                cleanups[0].NyxIdPending &&
                cleanups[0].VaultPending),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.NyxId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.Vault,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenOrphanVaultSucceedsAndNyxIdFails_KeepsNyxIdOnlyActorCleanup()
    {
        ArrangeSingleOrphanReplacement(
            nyxIdDeleted: false,
            vaultRevoked: true);
        var expectedSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-orphan");

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-fresh");
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 1 &&
                cleanups[0].ApiKeyId == "key-orphan" &&
                cleanups[0].SecretRef == expectedSecretRef &&
                cleanups[0].NyxIdPending &&
                cleanups[0].VaultPending),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.NyxId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.Vault,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCallerCancelsDuringLaterActorOwnedOrphanCleanup_PreservesCommittedFacts()
    {
        using var callerCancellation = new CancellationTokenSource();
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(
                Keys(
                    Key("key-orphan-a", ["wrong-service"]),
                    Key("key-orphan-b", ["wrong-service"])),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-orphan-a",
                Arg.Any<CancellationToken>())
            .Returns(false);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-orphan-b",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callerCancellation.Cancel();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return false;
            });
        _vault.RevokeAsync(
                Arg.Is<RevokeSecretRequest>(request =>
                    request.Ref == DeterministicSecretRef(
                        Owner("user-a"),
                        "key-orphan-a")),
                Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(false));
        PublishConfirmedCredential(stateVersion: 1);

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 2 &&
                cleanups.All(cleanup =>
                    cleanup.NyxIdPending &&
                    cleanup.VaultPending) &&
                cleanups.Select(cleanup => cleanup.ApiKeyId)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        new[] { "key-orphan-a", "key-orphan-b" },
                        StringComparer.Ordinal)),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCallerCancelsBetweenActorOwnedOrphanTracks_PreservesVaultPending()
    {
        using var callerCancellation = new CancellationTokenSource();
        var expectedSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-orphan");
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-orphan", ["wrong-service"])),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-orphan",
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callerCancellation.Cancel();
                return true;
            });
        _vault.RevokeAsync(
                Arg.Is<RevokeSecretRequest>(request =>
                    request.Ref == expectedSecretRef),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new RevokeSecretResult(false);
            });
        PublishConfirmedCredential(stateVersion: 1);

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 1 &&
                cleanups[0].ApiKeyId == "key-orphan" &&
                cleanups[0].SecretRef == expectedSecretRef &&
                cleanups[0].NyxIdPending &&
                cleanups[0].VaultPending),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.NyxId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-orphan",
            expectedSecretRef,
            ManagedCodexCredentialCleanupTrack.Vault,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenProjectedKeyIsValidAmongReservedKeys_RetainsItAndCleansOnlyOrphan()
    {
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(
                Key("key-orphan", ["us-sandbox", "us-llm"]),
                Key("key-a", ["us-sandbox", "us-llm"])));
        PublishConfirmedCredential(stateVersion: 4);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-a");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-orphan",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-a",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
        await _commands.Received(1).CommitPolicyReconciledAsync(
            "key-a",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-a"),
            Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenObsoleteCleanupFails_DoesNotBlockReadyCredential()
    {
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(false);
        var confirmed = Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        confirmed.PendingRevocations.Add(Cleanup("key-old"));
        _observation.PublishAfterDispatch(confirmed);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-a");
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>());
        await _nyxId.DidNotReceiveWithAnyArgs()
            .CreateApiKeyAsync(default!, default!, default);
        await _commands.Received(1).ConfirmReadinessAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-a"),
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("task-canceled")]
    public async Task EnsureReadyAsync_WhenReadyCleanupPreflightIsUnavailable_ConfirmsReadiness(
        string failureKind)
    {
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ManagedCodexNyxIdApiKey>>>(_ =>
            {
                throw CleanupFailure(failureKind);
            });
        var confirmed = Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        confirmed.PendingRevocations.Add(Cleanup("key-old"));
        _observation.PublishAfterDispatch(confirmed);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        ready.ApiKeyId.Should().Be("key-a");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-old",
            Arg.Any<string>(),
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).ConfirmReadinessAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-a"),
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadyCleanupPreflightHasProgrammingFault_Propagates()
    {
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ManagedCodexNyxIdApiKey>>>(_ =>
            {
                throw CleanupFailure("programming");
            });
        var confirmed = Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        confirmed.PendingRevocations.Add(Cleanup("key-old"));
        _observation.PublishAfterDispatch(confirmed);

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Cleanup invariant failed.");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await _commands.DidNotReceiveWithAnyArgs()
            .ConfirmReadinessAsync(default!, default!, default, default);
        await _commands.DidNotReceive().CompleteCleanupTrackAsync(
            Owner("user-a"),
            "key-old",
            Arg.Any<string>(),
            Arg.Any<ManagedCodexCredentialCleanupTrack>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadyCleanupHitsInternalTimeout_ConfirmsStructuralReadiness()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var lifecycle = CreateLifecycle(options: options);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverDeleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var testTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        CancellationToken cleanupToken = default;
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cleanupToken = call.Arg<CancellationToken>();
                cleanupEntered.TrySetResult();
                return neverDeleted.Task.WaitAsync(cleanupToken);
            });
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                confirmationRequested.TrySetResult();
                var confirmed = Snapshot(
                    call.Arg<ManagedCodexCredentialDescriptor>(),
                    stateVersion: 5,
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
                confirmed.PendingRevocations.Add(Cleanup("key-old"));
                _observation.Publish(confirmed);
                return Admission();
            });

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await cleanupEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(50));
        await confirmationRequested.Task.WaitAsync(testTimeout.Token);
        var ready = await readiness.WaitAsync(testTimeout.Token);

        cleanupToken.IsCancellationRequested.Should().BeTrue();
        ready.ApiKeyId.Should().Be("key-a");
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCallerCancelsBeforeReadyCleanup_PropagatesWithoutMutation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        CaptureCommittedCleanups(snapshot.PendingRevocations);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.GetCurrentUserIdAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callerCancellation.Cancel();
                return "user-a";
            });

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceiveWithAnyArgs()
            .ConfirmReadinessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCallerCancelsAtReadyCleanupBoundary_PropagatesBeforeMutation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var mutationStarted = false;
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        CaptureCommittedCleanups(snapshot.PendingRevocations);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callerCancellation.Cancel();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                mutationStarted = true;
                return true;
            });
        PublishConfirmedCredential(stateVersion: 5);

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        mutationStarted.Should().BeFalse();
        await _commands.DidNotReceiveWithAnyArgs()
            .ConfirmReadinessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceCleanupHitsInternalTimeout_ReturnsCommittedCredential()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var lifecycle = CreateLifecycle(options: options);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverDeleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var testTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
        snapshot.PendingRevocations.Add(Cleanup("key-old"));
        CaptureCommittedCleanups(snapshot.PendingRevocations);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(snapshot);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(Keys(Key("key-a", ["us-sandbox", "us-llm"])));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cleanupEntered.TrySetResult();
                return neverDeleted.Task.WaitAsync(call.Arg<CancellationToken>());
            });
        PublishConfirmedCredential(stateVersion: 5);

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await cleanupEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(100));

        var ready = await readiness.WaitAsync(testTimeout.Token);

        ready.ApiKeyId.Should().Be("key-a");
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenFirstCallsAreConcurrent_MutatesOnceAndBothReturnCommittedCredential()
    {
        var lifecycle = CreateLifecycle(
            mutationLease: new InMemoryManagedCodexCredentialMutationLease());
        var mutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                mutationEntered.TrySetResult();
                await releaseMutation.Task;
                return IssuedKey("key-new");
            });
        PublishConfirmedCredential(stateVersion: 1);

        var first = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await mutationEntered.Task;

        var second = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal);
        releaseMutation.TrySetResult();

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(value => value.ApiKeyId == "key-new");
        results.Should().OnlyContain(value =>
            value.Equals(_observation.LastPublished!.Credential));
        await _nyxId.Received(1).CreateApiKeyAsync(
            "user-bearer",
            Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadyCleanupProgresses_ConfirmsReadinessForBusyCaller()
    {
        await AssertReadyCleanupConfirmsConcurrentReadinessAsync(
            cleanupChangesProjectedState: true);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReadyCleanupIsNoOp_ConfirmsReadinessForBusyCaller()
    {
        await AssertReadyCleanupConfirmsConcurrentReadinessAsync(
            cleanupChangesProjectedState: false);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceWaitsBehindNormalCleanup_ReacquiresLeaseAndValidatesRemotely()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lifecycle = CreateLifecycle(
            mutationLease: new InMemoryManagedCodexCredentialMutationLease());
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        projected.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(Key("key-a", ["us-sandbox", "us-llm"])));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return true;
            });
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                    descriptor.ApiKeyId == "key-a"),
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var confirmed = Snapshot(
                    ReadyDescriptor(),
                    stateVersion: 5,
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
                confirmed.PendingRevocations.Add(Cleanup("key-old"));
                _observation.Publish(confirmed);
                return Admission();
            });
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                    descriptor.ApiKeyId == "key-a"),
                ManagedCodexCredentialReadinessEvidence.RemoteValidated,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _observation.Publish(
                    Snapshot(
                        ReadyDescriptor(),
                        stateVersion: 6,
                        ManagedCodexCredentialReadinessEvidence.RemoteValidated));
                return Admission();
            });

        var normalOwner = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            testTimeout.Token);
        await cleanupEntered.Task;
        var forceWaiter = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
            testTimeout.Token);
        releaseCleanup.TrySetResult();

        var results = await Task.WhenAll(normalOwner, forceWaiter);

        results.Should().OnlyContain(result => result.ApiKeyId == "key-a");
        await _nyxId.Received(2).ListApiKeysAsync(
            "user-bearer",
            Arg.Any<CancellationToken>());
        await _commands.Received(1).ConfirmReadinessAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-a"),
            ManagedCodexCredentialReadinessEvidence.RemoteValidated,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceWaitsBehindNormalCleanupWithoutBearer_FailsAuthorization()
    {
        var mutationLease = new RecordingManagedCodexCredentialMutationLease();
        var lifecycle = CreateLifecycle(
            mutationLease: mutationLease);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        projected.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return true;
            });
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                    descriptor.ApiKeyId == "key-a"),
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var confirmed = Snapshot(
                    ReadyDescriptor(),
                    stateVersion: 5,
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
                confirmed.PendingRevocations.Add(Cleanup("key-old"));
                _observation.Publish(confirmed);
                _observation.Complete();
                return Admission();
            });

        var normalOwner = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await cleanupEntered.Task;
        var forceWaiter = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await mutationLease.SecondAttempted;
        releaseCleanup.TrySetResult();

        (await normalOwner).ApiKeyId.Should().Be("key-a");
        var forceAct = async () => await forceWaiter;

        (await forceAct.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_user_authorization_unavailable");
        mutationLease.AttemptCount.Should().Be(3);
        var releasedLease = await mutationLease.TryAcquireAsync(
            ManagedCodexCredentialActorIdentity.From(Owner("user-a")));
        releasedLease.Should().NotBeNull();
        await releasedLease!.DisposeAsync();
        await _nyxId.Received(1).ListApiKeysAsync(
            "user-bearer",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenBearerlessForceWaiterObservesStructuralEvidenceBehindForceOwner_WaitsForRemoteValidation()
    {
        var mutationLease = new RecordingManagedCodexCredentialMutationLease();
        var lifecycle = CreateLifecycle(
            mutationLease: mutationLease);
        var validationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(Key("key-a", ["us-sandbox", "us-llm"])));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                validationEntered.TrySetResult();
                await releaseValidation.Task;
                var request = call.Arg<ResolveSecretRequest>();
                return new ResolveSecretResult(
                    Reference(request.Ref, request.OwnerScopeKey, Now.AddDays(30)),
                    RawKey);
            });
        _observation.PublishAfterDispatch(
            Snapshot(
                ReadyDescriptor(),
                stateVersion: 5,
                ManagedCodexCredentialReadinessEvidence.RemoteValidated));

        var first = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await validationEntered.Task;
        var second = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await mutationLease.SecondAttempted;
        _observation.Publish(
            Snapshot(
                ReadyDescriptor(),
                stateVersion: 4,
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed));
        var next = await Task.WhenAny(mutationLease.ThirdAttempted, second);

        next.Should().BeSameAs(mutationLease.ThirdAttempted);
        second.IsCompleted.Should().BeFalse();
        releaseValidation.TrySetResult();

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.ApiKeyId == "key-a");
        mutationLease.AttemptCount.Should().Be(3);
        await _commands.Received(1).ConfirmReadinessAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-a"),
            ManagedCodexCredentialReadinessEvidence.RemoteValidated,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceReacquiresFromNewerTriggerAndQueryLags_UsesTriggerSnapshot()
    {
        var mutationLease = Substitute.For<IManagedCodexCredentialMutationLease>();
        var acquiredLease =
            Substitute.For<IManagedCodexCredentialMutationLeaseHandle>();
        mutationLease.TryAcquireAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                (IManagedCodexCredentialMutationLeaseHandle?)null,
                acquiredLease);
        var lifecycle = CreateLifecycle(mutationLease);
        var lagging = Snapshot(ReadyDescriptor("key-old"), stateVersion: 4);
        var trigger = Snapshot(
            ReadyDescriptor("key-new"),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(lagging);
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(Keys(
                Key("key-old", ["us-sandbox", "us-llm"]),
                Key("key-new", ["us-sandbox", "us-llm"])));
        PublishConfirmedCredential(stateVersion: 6);
        _observation.Publish(trigger);

        var ready = await lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.Should().BeEquivalentTo(trigger.Credential);
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-new",
            Arg.Any<CancellationToken>());
        await _nyxId.Received(1).RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>());
        await _query.Received(3).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceReacquiresNearInitialWaitBoundary_UsesFreshLeaseAnchor()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var firstAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiredLease =
            Substitute.For<IManagedCodexCredentialMutationLeaseHandle>();
        var attempt = 0;
        _lease.TryAcquireAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attempt++;
                if (attempt == 1)
                {
                    firstAttempted.TrySetResult();
                    return (IManagedCodexCredentialMutationLeaseHandle?)null;
                }

                _time.Advance(TimeSpan.FromSeconds(20));
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return acquiredLease;
            });
        var trigger = Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        _query.ResolveAsync(
                Owner("user-a"),
                Arg.Any<CancellationToken>())
            .Returns(
                (ManagedCodexCredentialSnapshot?)null,
                (ManagedCodexCredentialSnapshot?)null,
                trigger);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(Keys(Key("key-a", ["us-sandbox", "us-llm"])));
        PublishConfirmedCredential(stateVersion: 6);
        var lifecycle = CreateLifecycle(options: options);

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await firstAttempted.Task;
        _time.Advance(TimeSpan.FromSeconds(50));
        _observation.Publish(trigger);

        var ready = await readiness;

        ready.ApiKeyId.Should().Be("key-a");
        attempt.Should().Be(2);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenForceLeaseReturnsAfterFreshAcquisitionBudget_DisposesAndTimesOut()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var acquiredLease =
            Substitute.For<IManagedCodexCredentialMutationLeaseHandle>();
        var attempt = 0;
        _lease.TryAcquireAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempt++;
                if (attempt == 1)
                    return (IManagedCodexCredentialMutationLeaseHandle?)null;

                _time.Advance(TimeSpan.FromSeconds(60));
                return acquiredLease;
            });
        _nyxId.GetCurrentUserIdAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return "user-a";
            });
        var trigger = Snapshot(
            ReadyDescriptor(),
            stateVersion: 5,
            ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
        var lifecycle = CreateLifecycle(options: options);

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        _observation.Publish(trigger);
        var act = async () => await readiness;

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_commit_timeout");
        attempt.Should().Be(2);
        await acquiredLease.Received(1).DisposeAsync();
        await _nyxId.DidNotReceiveWithAnyArgs()
            .ListApiKeysAsync(default!, default);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenStaleProjectionTreatsActorCurrentRemoteAsObsolete_DoesNotDeleteBeforeCommit()
    {
        var projected = Snapshot(
            ReadyDescriptor("key-projected"),
            stateVersion: 4);
        _query.ResolveAsync(
                Owner("user-a"),
                Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-actor-current", ["wrong-service"])),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        _commands.CommitRotatedAsync(
                "key-projected",
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _observation.Complete();
                return Admission();
            });

        var act = () => _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_commit_timeout");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-actor-current",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceive().RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == DeterministicSecretRef(
                    Owner("user-a"),
                    "key-actor-current")),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-actor-current"),
            Arg.Any<CancellationToken>());
        await _commands.Received(1).CommitRotatedAsync(
            "key-projected",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-fresh"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 1 &&
                cleanups[0].ApiKeyId == "key-actor-current" &&
                cleanups[0].NyxIdPending &&
                cleanups[0].VaultPending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenSoleRemoteIsAlreadyPendingCleanup_CreatesDistinctCredential()
    {
        var projected = Snapshot(
            ReadyDescriptor("key-projected"),
            stateVersion: 4);
        var pendingSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-pending");
        projected.PendingRevocations.Add(new ManagedCodexCredentialCleanup
        {
            ApiKeyId = "key-pending",
            SecretRef = pendingSecretRef,
            NyxIdPending = true,
            VaultPending = true,
            RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-5)),
        });
        CaptureCommittedCleanups(projected.PendingRevocations);
        _query.ResolveAsync(
                Owner("user-a"),
                Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-pending", ["us-sandbox", "us-llm"])),
                Keys(Key("key-distinct", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-distinct"));
        PublishConfirmedCredential(stateVersion: 5);

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

        ready.ApiKeyId.Should().Be("key-distinct");
        await _commands.Received(1).CommitRotatedAsync(
            "key-projected",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-distinct"),
            Arg.Any<ManagedCodexCredentialCleanup>(),
            Arg.Is<IReadOnlyList<ManagedCodexCredentialCleanup>>(cleanups =>
                cleanups.Count == 1 &&
                cleanups[0].ApiKeyId == "key-pending"),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().ConfirmReadinessAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-pending"),
            Arg.Any<ManagedCodexCredentialReadinessEvidence>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReplacementCleanupPublishesOldKey_BusyCallerWaitsForFinalCredential()
    {
        var lifecycle = CreateLifecycle(
            mutationLease: new InMemoryManagedCodexCredentialMutationLease());
        var mutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-a",
                Arg.Any<CancellationToken>())
            .Returns(false);
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                mutationEntered.TrySetResult();
                await releaseMutation.Task;
                return IssuedKey("key-replacement");
            });
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                    cleanup.ApiKeyId == "key-a"),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _observation.PublishNonReadiness(
                    Snapshot(ReadyDescriptor("key-a"), stateVersion: 4));
                return Admission();
            });
        PublishConfirmedCredential(stateVersion: 5);

        var first = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await mutationEntered.Task;
        var second = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        releaseMutation.TrySetResult();

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result =>
            result.ApiKeyId == "key-replacement");
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenReplacementCommitFails_BusyCallerRejectsCleanupOnlySnapshot()
    {
        var lifecycle = CreateLifecycle(
            mutationLease: new InMemoryManagedCodexCredentialMutationLease());
        var mutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-a", ["us-sandbox", "us-llm"])),
                Keys(Key("key-replacement", ["us-sandbox", "us-llm"])));
        _vault.ResolveAsync(
                Arg.Any<ResolveSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(
                null,
                null,
                SecretResolutionFailureReason.NotFound));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-a",
                Arg.Any<CancellationToken>())
            .Returns(false);
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                mutationEntered.TrySetResult();
                await releaseMutation.Task;
                return IssuedKey("key-replacement");
            });
        _commands.CommitRotatedAsync(
                "key-a",
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<DispatchAdmission>>(_ =>
                throw new InvalidOperationException("dispatch unavailable"));
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                    cleanup.ApiKeyId == "key-a"),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var stale = Snapshot(ReadyDescriptor("key-a"), stateVersion: 4);
                stale.PendingRevocations.Add(Cleanup("key-a"));
                _observation.PublishNonReadiness(stale);
                return Admission();
            });

        var owner = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        await mutationEntered.Task;
        var busy = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation);
        releaseMutation.TrySetResult();

        var ownerAct = async () => await owner;
        (await ownerAct.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_persistence_pending");
        _observation.Complete();
        var busyAct = async () => await busy;
        (await busyAct.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_commit_timeout");
        await _nyxId.DidNotReceive().RevokeApiKeyAsync(
            "user-bearer",
            "key-a",
            Arg.Any<CancellationToken>());
        await _vault.DidNotReceive().RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == ReadyDescriptor().SecretReference.Ref),
            Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().QueueCleanupAsync(
            Owner("user-a"),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCallerCancelsAfterIrreversibleMutation_CompletesAndReturnsCommittedCredential()
    {
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken mutationToken = default;
        CancellationToken vaultToken = default;
        CancellationToken commandToken = default;
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                mutationToken = call.Arg<CancellationToken>();
                callerCancellation.Cancel();
                return IssuedKey("key-new");
            });
        _vault.PutAsync(
                Arg.Any<StoreSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<StoreSecretRequest>();
                vaultToken = call.Arg<CancellationToken>();
                vaultToken.ThrowIfCancellationRequested();
                return new StoreSecretResult(Reference(
                    request.RequestedRef!,
                    request.OwnerScopeKey,
                    request.ExpiresAt!.Value));
            });
        _commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<IReadOnlyList<ManagedCodexCredentialCleanup>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                commandToken = call.Arg<CancellationToken>();
                commandToken.ThrowIfCancellationRequested();
                _observation.Publish(
                    Snapshot(
                        call.Arg<ManagedCodexCredentialDescriptor>(),
                        stateVersion: 1));
                return Admission();
            });

        var ready = await _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);

        ready.ApiKeyId.Should().Be("key-new");
        mutationToken.Should().NotBe(callerCancellation.Token);
        vaultToken.Should().Be(mutationToken);
        commandToken.Should().Be(mutationToken);
        mutationToken.CanBeCanceled.Should().BeTrue();
        mutationToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenPrimaryMutationTimesOut_ReservesLiveCompensationAndRecording()
    {
        var vaultEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverStored = new TaskCompletionSource<StoreSecretResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ManagedCodexCredentialCleanup? queued = null;
        CancellationToken compensationToken = default;
        CancellationToken cleanupToken = default;
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _vault.PutAsync(
                Arg.Any<StoreSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                vaultEntered.TrySetResult();
                return neverStored.Task.WaitAsync(call.Arg<CancellationToken>());
            });
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-new",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                compensationToken = call.Arg<CancellationToken>();
                compensationToken.ThrowIfCancellationRequested();
                return false;
            });
        _vault.RevokeAsync(
                Arg.Any<RevokeSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(false));
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                queued = call.Arg<ManagedCodexCredentialCleanup>().Clone();
                cleanupToken = call.Arg<CancellationToken>();
                cleanupToken.ThrowIfCancellationRequested();
                return Admission();
            });

        var readiness = _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await vaultEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(
            ManagedCodexOptionsValidatorTests.ValidOptions().MutationCompletionSeconds));
        var act = async () => await readiness;

        await act.Should().ThrowAsync<OperationCanceledException>();
        queued.Should().NotBeNull();
        queued!.ApiKeyId.Should().Be("key-new");
        queued.NyxIdPending.Should().BeTrue();
        queued.VaultPending.Should().BeTrue();
        compensationToken.CanBeCanceled.Should().BeTrue();
        compensationToken.IsCancellationRequested.Should().BeFalse();
        cleanupToken.CanBeCanceled.Should().BeTrue();
        cleanupToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCancellationCompensationStartsAfterElapsedWork_UsesLeaseBoundReserve()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var lifecycle = CreateLifecycle(options: options);
        var compensationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverDeleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken compensationToken = default;
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _vault.PutAsync(
                Arg.Any<StoreSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _time.Advance(TimeSpan.FromSeconds(40));
                return Task.FromException<StoreSecretResult>(
                    new OperationCanceledException("simulated interrupted store"));
            });
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-new",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                compensationToken = call.Arg<CancellationToken>();
                compensationEntered.TrySetResult();
                return neverDeleted.Task.WaitAsync(compensationToken);
            });

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await compensationEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(20));
        var liveBeforeCompensationBoundary =
            !compensationToken.IsCancellationRequested;
        _time.Advance(TimeSpan.FromSeconds(40));
        var cancelledAtCompensationBoundary =
            compensationToken.IsCancellationRequested;
        var act = async () => await readiness;

        await act.Should().ThrowAsync<OperationCanceledException>();
        liveBeforeCompensationBoundary.Should().BeTrue();
        cancelledAtCompensationBoundary.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCleanupRecordingStartsAfterElapsedWork_UsesFinalLeaseReserve()
    {
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.MutationCompletionSeconds = 60;
        options.MutationLeaseSeconds = 120;
        var lifecycle = CreateLifecycle(options: options);
        var recordingEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRecorded = new TaskCompletionSource<DispatchAdmission>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken recordingToken = default;
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(),
                Keys(Key("key-new", ["us-sandbox", "us-llm"])));
        _vault.PutAsync(
                Arg.Any<StoreSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _time.Advance(TimeSpan.FromSeconds(40));
                return Task.FromException<StoreSecretResult>(
                    new OperationCanceledException("simulated interrupted store"));
            });
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-new",
                Arg.Any<CancellationToken>())
            .Returns(false);
        _vault.RevokeAsync(
                Arg.Any<RevokeSecretRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(false));
        _commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordingToken = call.Arg<CancellationToken>();
                recordingEntered.TrySetResult();
                return neverRecorded.Task.WaitAsync(recordingToken);
            });

        var readiness = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await recordingEntered.Task;
        _time.Advance(TimeSpan.FromSeconds(60));
        var liveBeforeRecordingBoundary = !recordingToken.IsCancellationRequested;
        _time.Advance(TimeSpan.FromSeconds(10));
        var cancelledAtRecordingBoundary = recordingToken.IsCancellationRequested;
        var act = async () => await readiness;

        (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>())
            .Which.Code.Should().Be("managed_credential_persistence_pending");
        liveBeforeRecordingBoundary.Should().BeTrue();
        cancelledAtRecordingBoundary.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenBusyWaitCallerCancels_PropagatesCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        _lease.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IManagedCodexCredentialMutationLeaseHandle?)null);

        var readiness = _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            bearerToken: null,
            ManagedCodexCredentialReadinessMode.Normal,
            callerCancellation.Token);
        callerCancellation.Cancel();
        var act = async () => await readiness;

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task AssertReadyCleanupConfirmsConcurrentReadinessAsync(
        bool cleanupChangesProjectedState)
    {
        var lifecycle = CreateLifecycle(
            mutationLease: new InMemoryManagedCodexCredentialMutationLease());
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var testTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var projected = Snapshot(ReadyDescriptor(), stateVersion: 3);
        projected.PendingRevocations.Add(Cleanup("key-old"));
        _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
            .Returns(projected);
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-old",
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return true;
            });
        _commands.CompleteCleanupTrackAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-old",
                "sec-key-old",
                ManagedCodexCredentialCleanupTrack.NyxId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var cleanupSnapshot = Snapshot(
                    ReadyDescriptor(),
                    stateVersion: 4);
                if (!cleanupChangesProjectedState)
                    cleanupSnapshot.PendingRevocations.Add(Cleanup("key-old"));
                _observation.PublishNonReadiness(cleanupSnapshot);
                cleanupPublished.TrySetResult();
                return Admission();
            });
        _commands.ConfirmReadinessAsync(
                Owner("user-a"),
                Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                    descriptor.ApiKeyId == "key-a"),
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                confirmationRequested.TrySetResult();
                await releaseConfirmation.Task;
                var confirmed = Snapshot(
                    ReadyDescriptor(),
                    stateVersion: 5,
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
                if (!cleanupChangesProjectedState)
                    confirmed.PendingRevocations.Add(Cleanup("key-old"));
                _observation.Publish(confirmed);
                return Admission();
            });

        var owner = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);
        await cleanupEntered.Task.WaitAsync(testTimeout.Token);
        var busy = lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "user-bearer",
            ManagedCodexCredentialReadinessMode.Normal);

        releaseCleanup.TrySetResult();
        await cleanupPublished.Task.WaitAsync(testTimeout.Token);

        busy.IsCompleted.Should().BeFalse();
        var next = await Task.WhenAny(confirmationRequested.Task, owner)
            .WaitAsync(testTimeout.Token);
        next.Should().BeSameAs(confirmationRequested.Task);
        releaseConfirmation.TrySetResult();

        var results = await Task.WhenAll(owner, busy)
            .WaitAsync(testTimeout.Token);

        results.Should().OnlyContain(result => result.ApiKeyId == "key-a");
    }

    private ManagedCodexCredentialLifecycle CreateLifecycle(
        IManagedCodexCredentialMutationLease? mutationLease = null,
        ManagedCodexOptions? options = null) =>
        new(
            Options.Create(options ?? ManagedCodexOptionsValidatorTests.ValidOptions()),
            _nyxId,
            _vault,
            _query,
            _commands,
            mutationLease ?? _lease,
            _observation,
            _time,
            NullLogger<ManagedCodexCredentialLifecycle>.Instance);

    private DispatchAdmission Dispatch()
    {
        _observation.RecordDispatch();
        return Admission();
    }

    private void PublishConfirmedCredential(long stateVersion)
    {
        _commands.ConfirmReadinessAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<ManagedCodexCredentialReadinessEvidence>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var snapshot = Snapshot(
                    call.Arg<ManagedCodexCredentialDescriptor>(),
                    stateVersion,
                    call.Arg<ManagedCodexCredentialReadinessEvidence>());
                snapshot.PendingRevocations.Add(
                    _committedPendingCleanups.Select(static cleanup => cleanup.Clone()));
                _observation.Publish(snapshot);
                return Admission();
            });
    }

    private void CaptureCommittedCleanups(
        IEnumerable<ManagedCodexCredentialCleanup> cleanups,
        bool preferNyxIdTrack = false)
    {
        foreach (var source in cleanups
                     .OrderBy(static cleanup => cleanup.ApiKeyId, StringComparer.Ordinal)
                     .ThenBy(static cleanup => cleanup.SecretRef, StringComparer.Ordinal))
        {
            var cleanup = source.Clone();
            var nyxIdOwner = cleanup.NyxIdPending
                ? _committedPendingCleanups.FirstOrDefault(item =>
                    item.NyxIdPending &&
                    string.Equals(
                        item.ApiKeyId,
                        cleanup.ApiKeyId,
                        StringComparison.Ordinal))
                : null;
            var existing = _committedPendingCleanups.SingleOrDefault(item =>
                string.Equals(
                    item.ApiKeyId,
                    cleanup.ApiKeyId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.SecretRef,
                    cleanup.SecretRef,
                    StringComparison.Ordinal));
            if (existing is null)
            {
                if (nyxIdOwner is not null)
                {
                    if (preferNyxIdTrack)
                        nyxIdOwner.NyxIdPending = false;
                    else
                        cleanup.NyxIdPending = false;
                }
                if (cleanup.NyxIdPending || cleanup.VaultPending)
                    _committedPendingCleanups.Add(cleanup);
                continue;
            }

            if (cleanup.NyxIdPending)
            {
                if (preferNyxIdTrack && nyxIdOwner is not null &&
                    !ReferenceEquals(nyxIdOwner, existing))
                {
                    nyxIdOwner.NyxIdPending = false;
                }
                existing.NyxIdPending |=
                    nyxIdOwner is null ||
                    ReferenceEquals(nyxIdOwner, existing) ||
                    preferNyxIdTrack;
            }
            existing.VaultPending |= cleanup.VaultPending;
            if (existing.RequestedAt is null)
                existing.RequestedAt = cleanup.RequestedAt?.Clone();
        }
    }

    private void ArrangeReplacementWithPostCommitCleanupFailure(
        string failureKind)
    {
        var listAttempt = 0;
        _nyxId.ListApiKeysAsync(
                "user-bearer",
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                listAttempt++;
                return listAttempt switch
                {
                    1 => Keys(Key("key-orphan", ["wrong-service"])),
                    2 => Keys(Key("key-new", ["us-sandbox", "us-llm"])),
                    _ => throw CleanupFailure(failureKind),
                };
            });
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-new"));
        PublishConfirmedCredential(stateVersion: 1);
    }

    private void ArrangeSingleOrphanReplacement(
        bool nyxIdDeleted,
        bool vaultRevoked)
    {
        var expectedSecretRef = DeterministicSecretRef(
            Owner("user-a"),
            "key-orphan");
        _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns(
                Keys(Key("key-orphan", ["wrong-service"])),
                Keys(Key("key-fresh", ["us-sandbox", "us-llm"])));
        _nyxId.RevokeApiKeyAsync(
                "user-bearer",
                "key-orphan",
                Arg.Any<CancellationToken>())
            .Returns(nyxIdDeleted);
        _vault.RevokeAsync(
                Arg.Is<RevokeSecretRequest>(request =>
                    request.Ref == expectedSecretRef),
                Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(vaultRevoked));
        _nyxId.CreateApiKeyAsync(
                "user-bearer",
                Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(IssuedKey("key-fresh"));
        PublishConfirmedCredential(stateVersion: 1);
    }

    private static Exception CleanupFailure(string failureKind) =>
        failureKind switch
        {
            "managed" => new ManagedCodexCredentialLifecycleException(
                "managed_api_key_list_invalid",
                "NyxID returned an invalid API-key list."),
            "http" => new HttpRequestException("NyxID unavailable."),
            "timeout" => new TimeoutException("NyxID timed out."),
            "task-canceled" => new TaskCanceledException(
                "NyxID request timed out."),
            "programming" => new InvalidOperationException(
                "Cleanup invariant failed."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported cleanup failure kind."),
        };

    private static DispatchAdmission Admission() =>
        new(
            true,
            "command-1",
            Now,
            "managed-codex-credential:nyxid::user-a",
            "command-1");

    private static ExternalSubjectRef Owner(string userId) => new()
    {
        Platform = "nyxid",
        Tenant = string.Empty,
        ExternalUserId = userId,
    };

    private static ManagedCodexCredentialSnapshot Snapshot(
        ManagedCodexCredentialDescriptor descriptor,
        long stateVersion,
        ManagedCodexCredentialReadinessEvidence readinessEvidence =
            ManagedCodexCredentialReadinessEvidence.RemoteValidated) =>
        new()
        {
            Credential = descriptor.Clone(),
            StateVersion = stateVersion,
            LastEventId = $"event-{stateVersion}",
            ReadinessEvidence = readinessEvidence,
        };

    private static ManagedCodexCredentialDescriptor ReadyDescriptor(
        string apiKeyId = "key-a") =>
        new()
        {
            Owner = Owner("user-a"),
            ApiKeyId = apiKeyId,
            SecretReference = Reference(
                $"sec-{apiKeyId}",
                "managed-codex-credential:nyxid::user-a",
                Now.AddDays(30)),
            ManagedCodexUserServiceId = "us-sandbox",
            ChronoLlmUserServiceId = "us-llm",
            ManagedCodexServiceSlug = "chrono-sandbox",
            ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddDays(30)),
            Status = ManagedCodexCredentialStatus.Active,
        };

    private static SecretReference Reference(
        string reference,
        string ownerScopeKey,
        DateTimeOffset expiresAt) =>
        new()
        {
            Ref = reference,
            Purpose = CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            OwnerScopeKey = ownerScopeKey,
            Fingerprint = "fingerprint",
            Version = 1,
            CreatedAtUnixMs = Now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        };

    private static ManagedCodexCredentialCleanup Cleanup(string apiKeyId) =>
        new()
        {
            ApiKeyId = apiKeyId,
            SecretRef = $"sec-{apiKeyId}",
            NyxIdPending = true,
            RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-5)),
        };

    private static IReadOnlyList<ManagedCodexNyxIdService> UserServices(
        string sandboxId,
        string llmId) =>
        [
            new ManagedCodexNyxIdService(
                sandboxId,
                ManagedCodexOptions.ManagedCodexServiceSlug,
                true,
                "personal",
                null,
                false,
                true,
                "proxy:* sandbox:execute"),
            new ManagedCodexNyxIdService(
                llmId,
                ManagedCodexOptions.ChronoLlmServiceSlug,
                true,
                "personal",
                null,
                null,
                null,
                null),
        ];

    private static IReadOnlyList<ManagedCodexNyxIdApiKey> Keys(
        params ManagedCodexNyxIdApiKey[] keys) =>
        keys;

    private static ManagedCodexNyxIdApiKey Key(
        string id,
        IReadOnlyList<string> allowedServiceIds,
        bool isActive = true,
        DateTimeOffset? expiresAt = null) =>
        new(
            id,
            "aevatar-managed-codex",
            "proxy",
            "codex",
            isActive,
            false,
            allowedServiceIds,
            false,
            [],
            expiresAt ?? Now.AddDays(30));

    private static ManagedCodexNyxIdIssuedApiKey IssuedKey(string id) =>
        new(
            Key(id, ["us-sandbox", "us-llm"]),
            new ManagedCodexOpaqueSecret(RawKey));

    private static string DeterministicSecretRef(
        ExternalSubjectRef owner,
        string apiKeyId)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ownerScopeKey}\n{apiKeyId}"));
        return "sec_managed_codex_" + Convert.ToHexStringLower(digest);
    }

    private sealed class RecordingManagedCodexCredentialMutationLease :
        IManagedCodexCredentialMutationLease
    {
        private readonly InMemoryManagedCodexCredentialMutationLease _inner = new();
        private readonly TaskCompletionSource _secondAttempted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _thirdAttempted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public Task SecondAttempted => _secondAttempted.Task;
        public Task ThirdAttempted => _thirdAttempted.Task;

        public async ValueTask<IManagedCodexCredentialMutationLeaseHandle?>
            TryAcquireAsync(
                string ownerKey,
                CancellationToken ct = default)
        {
            var lease = await _inner.TryAcquireAsync(ownerKey, ct);
            switch (Interlocked.Increment(ref _attemptCount))
            {
                case 2:
                    _secondAttempted.TrySetResult();
                    break;
                case 3:
                    _thirdAttempted.TrySetResult();
                    break;
            }

            return lease;
        }
    }

    private sealed class RecordingManagedCodexReadinessObservationPort :
        IManagedCodexCredentialReadinessObservationPort
    {
        private readonly object _gate = new();
        private readonly List<RecordingLease> _leases = [];
        private readonly List<ManagedCodexCredentialSnapshot> _history = [];
        private ManagedCodexCredentialSnapshot? _publishAfterDispatch;
        private bool _completed;

        public ManagedCodexCredentialSnapshot? LastPublished { get; private set; }

        public Task<IManagedCodexCredentialReadinessObservationLease> BindAsync(
            ExternalSubjectRef owner,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var lease = new RecordingLease(Remove);
            lock (_gate)
            {
                foreach (var snapshot in _history)
                    lease.Publish(snapshot);
                if (_completed)
                    lease.Complete();
                else
                    _leases.Add(lease);
            }
            return Task.FromResult<IManagedCodexCredentialReadinessObservationLease>(lease);
        }

        public void Publish(ManagedCodexCredentialSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var published = snapshot.Clone();
            RecordingLease[] leases;
            lock (_gate)
            {
                LastPublished = published.Clone();
                _history.Add(published.Clone());
                leases = _leases.ToArray();
            }
            foreach (var lease in leases)
                lease.Publish(published);
        }

        public void PublishNonReadiness(ManagedCodexCredentialSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
        }

        public void PublishAfterDispatch(ManagedCodexCredentialSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            lock (_gate)
                _publishAfterDispatch = snapshot.Clone();
        }

        public void Complete()
        {
            RecordingLease[] leases;
            lock (_gate)
            {
                _completed = true;
                leases = _leases.ToArray();
                _leases.Clear();
            }
            foreach (var lease in leases)
                lease.Complete();
        }

        internal void RecordDispatch()
        {
            ManagedCodexCredentialSnapshot? pending;
            lock (_gate)
            {
                pending = _publishAfterDispatch;
                _publishAfterDispatch = null;
            }
            if (pending is not null)
                Publish(pending);
        }

        private void Remove(RecordingLease lease)
        {
            lock (_gate)
                _leases.Remove(lease);
        }

        private sealed class RecordingLease(
            Action<RecordingLease> remove) :
            IManagedCodexCredentialReadinessObservationLease
        {
            private readonly Channel<ManagedCodexCredentialSnapshot> _snapshots =
                Channel.CreateUnbounded<ManagedCodexCredentialSnapshot>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false,
                    });
            private int _disposed;

            public async IAsyncEnumerable<ManagedCodexCredentialSnapshot> ReadAllAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(ct))
                    yield return snapshot.Clone();
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    remove(this);
                    _snapshots.Writer.TryComplete();
                }
                return ValueTask.CompletedTask;
            }

            public void Publish(ManagedCodexCredentialSnapshot snapshot) =>
                _snapshots.Writer.TryWrite(snapshot.Clone());

            public void Complete() => _snapshots.Writer.TryComplete();
        }
    }
}
