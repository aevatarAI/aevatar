using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class NyxIdActionReadAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssueAsync_SameRequestIdentity_ShouldReuseOpaqueReferenceWithoutExtendingExpiry()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = new NyxIdActionReadAuthorityPort(
            vault,
            clock,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(24));

        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        clock.Advance(TimeSpan.FromMinutes(2));
        var replay = await port.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        first.Succeeded.Should().BeTrue();
        replay.Succeeded.Should().BeTrue();
        replay.Authority.Should().BeEquivalentTo(first.Authority);
        replay.Authority!.SecretRef.Should().NotContain("bearer");
        replay.Authority.SecretRef.Should().NotContain("owner-alpha");
        replay.Authority.SecretRef.Should().NotContain("scope-alpha");
        replay.Authority.ExpiresAtUnixMs.Should().Be(
            Now.AddMinutes(10).ToUnixTimeMilliseconds());

        var resolved = await port.ResolveAsync(
            replay.Authority,
            "scope-alpha",
            "owner-alpha");

        first.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        replay.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        resolved.Resolved.Should().BeTrue();
        ResolveBearerToken(resolved).Should().Be("bearer-original-alpha",
            "an idempotent replay cannot replace the authority accepted by the first request");
    }

    [Fact]
    public async Task IssueAsync_DifferentRequestIdentity_ShouldIssueDifferentAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var port = CreatePort(new InMemorySecretVault(clock), clock);

        var first = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var second = await port.IssueAsync(
            "bearer-beta",
            "scope-alpha",
            "owner-alpha",
            "command-action-beta");

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Authority!.SecretRef.Should().NotBe(first.Authority!.SecretRef);
    }

    [Fact]
    public async Task IssueAsync_ExpiredRequestWithDurableFence_ShouldNotReissueAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        clock.Advance(TimeSpan.FromMinutes(11));
        vault.PurgeAuthorityRecords();
        var restartedPort = CreatePort(vault, clock);
        var authorityReadsBeforeReplay = vault.AuthorityResolveCalls;
        var authorityWritesBeforeReplay = vault.AuthorityPutCalls;
        var replay = await restartedPort.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        replay.Status.Should().Be(
            NyxIdActionReadAuthorityIssueStatus.ReplayOnlyExpired);
        replay.Succeeded.Should().BeFalse();
        replay.FailureCode.Should().BeNull();
        replay.Authority.Should().BeEquivalentTo(first.Authority);
        replay.Authority!.ExpiresAtUnixMs.Should().Be(
            Now.AddMinutes(10).ToUnixTimeMilliseconds());
        vault.AuthorityResolveCalls.Should().Be(authorityReadsBeforeReplay,
            "replay-only evidence must not recover the bearer secret");
        vault.AuthorityPutCalls.Should().Be(authorityWritesBeforeReplay,
            "replay-only evidence must not issue a replacement authority");

        var freshWake = await restartedPort.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-beta");

        first.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        freshWake.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        freshWake.Succeeded.Should().BeTrue(
            "a newly authenticated state-change wake has a new client request identity");
    }

    [Fact]
    public async Task IssueAsync_GarnetStyleDeletedAuthorityAfterRevoke_ShouldRemainRevokedBeyondReplayWindow()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock)
        {
            DeleteAuthorityRecordOnSuccessfulRevoke = true,
        };
        var port = CreatePort(vault, clock);
        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var revoked = await port.RevokeAsync(
            first.Authority,
            "scope-alpha",
            "owner-alpha");
        clock.Advance(TimeSpan.FromHours(25));
        var restartedPort = CreatePort(vault, clock);
        var authorityReadsBeforeReplay = vault.AuthorityResolveCalls;
        var authorityWritesBeforeReplay = vault.AuthorityPutCalls;

        var replay = await restartedPort.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        revoked.Should().BeTrue();
        replay.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        replay.Succeeded.Should().BeFalse();
        replay.Authority.Should().BeNull();
        replay.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
        vault.AuthorityResolveCalls.Should().Be(authorityReadsBeforeReplay,
            "revocation must be proven without recovering bearer material");
        vault.AuthorityPutCalls.Should().Be(authorityWritesBeforeReplay,
            "a revoked request identity cannot receive a replacement authority");
    }

    [Fact]
    public async Task IssueAsync_RevokeAfterInitialMisses_ShouldNotPublishPostRevokeAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new RevocationRaceActionAuthorityVault(
            clock,
            SecretResolutionFailureReason.NotFound);
        var port = CreatePort(vault, clock);
        var authority = new NyxIdReadAuthorityRef
        {
            SecretRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                "scope-alpha",
                "owner-alpha",
                "command-action-alpha"),
            Purpose = CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            Version = 1,
            ExpiresAtUnixMs = Now.AddMinutes(10).ToUnixTimeMilliseconds(),
        };
        var fenceRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var issueTask = port.IssueAsync(
            "bearer-racing-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        await vault.AuthorityResolutionPauseReached;

        var revoked = await port.RevokeAsync(
            authority,
            "scope-alpha",
            "owner-alpha");
        vault.ReleaseAuthorityResolution();
        var issue = await issueTask;
        var authorityRecord = await vault.ResolveAsync(new ResolveSecretRequest(
            authority.SecretRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            "test-resolve-authority"));
        var fenceRecord = await vault.ResolveAsync(new ResolveSecretRequest(
            fenceRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            "scope-alpha",
            "owner-alpha",
            "test-resolve-fence"));

        revoked.Should().BeTrue();
        issue.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        issue.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
        issue.Authority.Should().BeNull();
        authorityRecord.Resolved.Should().BeFalse(
            "a revoke that linearized first cannot leave bearer material readable");
        fenceRecord.Resolved.Should().BeFalse(
            "a revoke that linearized first cannot leave replay evidence from the racing issue");
    }

    [Fact]
    public async Task IssueAsync_RevokeAfterExistingAuthorityRead_ShouldNotPublishFenceOrActiveResult()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new RevocationRaceActionAuthorityVault(
            clock,
            SecretResolutionFailureReason.None);
        var port = CreatePort(vault, clock);
        var authority = await StoreAuthorityAsync(vault, "bearer-existing-alpha");
        var fenceRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var issueTask = port.IssueAsync(
            "bearer-racing-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        await vault.AuthorityResolutionPauseReached;

        var revoked = await port.RevokeAsync(
            authority,
            "scope-alpha",
            "owner-alpha");
        vault.ReleaseAuthorityResolution();
        var issue = await issueTask;
        var authorityRecord = await ResolveAuthorityRecordAsync(vault, authority.SecretRef);
        var fenceRecord = await ResolveFenceRecordAsync(vault, fenceRef);

        revoked.Should().BeTrue();
        issue.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        issue.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
        issue.Authority.Should().BeNull();
        authorityRecord.Resolved.Should().BeFalse();
        fenceRecord.Resolved.Should().BeFalse(
            "an authority deleted by revoke cannot be republished through a stale existing read");
    }

    [Fact]
    public async Task IssueAsync_RevokeDuringRaceRecovery_ShouldNotPublishFenceOrActiveResult()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new RevocationRaceActionAuthorityVault(
            clock,
            SecretResolutionFailureReason.None)
        {
            StoreAuthorityThenFailFirstPut = true,
        };
        var port = CreatePort(vault, clock);
        var authority = CreateExpectedAuthority();
        var fenceRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var issueTask = port.IssueAsync(
            "bearer-racing-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        await vault.AuthorityResolutionPauseReached;

        var revoked = await port.RevokeAsync(
            authority,
            "scope-alpha",
            "owner-alpha");
        vault.ReleaseAuthorityResolution();
        var issue = await issueTask;
        var authorityRecord = await ResolveAuthorityRecordAsync(vault, authority.SecretRef);
        var fenceRecord = await ResolveFenceRecordAsync(vault, fenceRef);

        revoked.Should().BeTrue();
        issue.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        issue.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
        issue.Authority.Should().BeNull();
        authorityRecord.Resolved.Should().BeFalse();
        fenceRecord.Resolved.Should().BeFalse(
            "race recovery must recheck revocation after publishing its recovered fence");
    }

    [Theory]
    [InlineData("purpose", NyxIdActionReadAuthorityPort.PurposeMismatchCode)]
    [InlineData("scope", NyxIdActionReadAuthorityPort.ScopeMismatchCode)]
    [InlineData("owner", NyxIdActionReadAuthorityPort.OwnerMismatchCode)]
    [InlineData("request", NyxIdActionReadAuthorityPort.InvalidCode)]
    [InlineData("version", NyxIdActionReadAuthorityPort.InvalidCode)]
    public async Task IssueAsync_ExpiredFenceWithInvalidBinding_ShouldFailClosed(
        string mutation,
        string expectedFailureCode)
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        clock.Advance(TimeSpan.FromMinutes(11));
        vault.FenceResolutionTransform = result => MutateFence(result, mutation);

        var replay = await port.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        first.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        replay.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        replay.Succeeded.Should().BeFalse();
        replay.Authority.Should().BeNull();
        replay.FailureCode.Should().Be(expectedFailureCode);
    }

    [Fact]
    public async Task IssueAsync_DurableFenceVaultFailure_ShouldFailClosed()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        vault.FenceResolutionTransform = _ => new ResolveSecretResult(
            null,
            null,
            SecretResolutionFailureReason.InvalidRecord);

        var replay = await port.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        first.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Active);
        replay.Status.Should().Be(NyxIdActionReadAuthorityIssueStatus.Failed);
        replay.Succeeded.Should().BeFalse();
        replay.Authority.Should().BeNull();
        replay.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.UnavailableCode);
    }

    [Fact]
    public async Task ResolveAsync_NewPortInstance_ShouldResolveUnexpiredAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var issued = await CreatePort(vault, clock).IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var resolved = await CreatePort(vault, clock).ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        resolved.Resolved.Should().BeTrue();
        ResolveBearerToken(resolved).Should().Be("bearer-alpha");
    }

    [Fact]
    public async Task ResolveAsync_UnexpiredReferenceWithMissingVaultRecord_ShouldReturnMissing()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        vault.PurgeAuthorityRecords();
        var resolved = await port.ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        resolved.Resolved.Should().BeFalse();
        resolved.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.MissingCode);
    }

    [Fact]
    public async Task IssueAsync_ShouldPreserveBearerWithoutNormalization()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = CreatePort(vault, clock);
        var bearer = " bearer-alpha ";

        var issued = await port.IssueAsync(
            bearer,
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var stored = await vault.ResolveAsync(new ResolveSecretRequest(
            issued.Authority!.SecretRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            "test-resolve"));
        var resolved = await port.ResolveAsync(issued.Authority, "scope-alpha", "owner-alpha");

        stored.Secret.Should().Be(bearer);
        resolved.ToString().Should().NotContain(bearer);
    }

    [Fact]
    public async Task ResolveAsync_InvalidBindings_ShouldFailClosedWithStableCodes()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = CreatePort(vault, clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var authority = issued.Authority!;

        (await port.ResolveAsync(null, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.MissingCode);
        (await port.ResolveAsync(authority, "scope-other", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.ScopeMismatchCode);
        (await port.ResolveAsync(authority, "scope-alpha", "owner-other"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.OwnerMismatchCode);

        var wrongPurpose = authority.Clone();
        wrongPurpose.Purpose = "wrong-purpose";
        (await port.ResolveAsync(wrongPurpose, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.PurposeMismatchCode);

        clock.Advance(TimeSpan.FromMinutes(11));
        (await port.ResolveAsync(authority, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.ExpiredCode);
    }

    [Fact]
    public async Task RevokeAsync_ShouldMakeAuthorityUnresolvable()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock)
        {
            DeleteAuthorityRecordOnSuccessfulRevoke = true,
        };
        var port = CreatePort(vault, clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var revoked = await port.RevokeAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");
        var resolved = await port.ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        revoked.Should().BeTrue();
        resolved.Resolved.Should().BeFalse();
        resolved.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
    }

    private static NyxIdActionReadAuthorityPort CreatePort(
        ISecretVault vault,
        TimeProvider clock) =>
        new(
            vault,
            clock,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(24));

    private static string? ResolveBearerToken(
        NyxIdActionReadAuthorityResolution resolution)
    {
        var context = AgentToolExecutionContextMapper.FromPayload(
            resolution.CloneTransientToolContext());
        return AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials);
    }

    private static async Task<NyxIdReadAuthorityRef> StoreAuthorityAsync(
        ISecretVault vault,
        string bearer)
    {
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            bearer,
            "test-seed-authority",
            Now.AddMinutes(10),
            NyxIdActionReadAuthorityPort.BuildRequestedRef(
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                "scope-alpha",
                "owner-alpha",
                "command-action-alpha")));
        return new NyxIdReadAuthorityRef
        {
            SecretRef = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            ScopeId = stored.Reference.OwnerScopeKey,
            OwnerSubject = "owner-alpha",
            Version = stored.Reference.Version,
            ExpiresAtUnixMs = stored.Reference.ExpiresAtUnixMs,
        };
    }

    private static NyxIdReadAuthorityRef CreateExpectedAuthority() =>
        new()
        {
            SecretRef = NyxIdActionReadAuthorityPort.BuildRequestedRef(
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                "scope-alpha",
                "owner-alpha",
                "command-action-alpha"),
            Purpose = CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            Version = 1,
            ExpiresAtUnixMs = Now.AddMinutes(10).ToUnixTimeMilliseconds(),
        };

    private static Task<ResolveSecretResult> ResolveAuthorityRecordAsync(
        ISecretVault vault,
        string authorityRef) =>
        vault.ResolveAsync(new ResolveSecretRequest(
            authorityRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            "test-resolve-authority"));

    private static Task<ResolveSecretResult> ResolveFenceRecordAsync(
        ISecretVault vault,
        string fenceRef) =>
        vault.ResolveAsync(new ResolveSecretRequest(
            fenceRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
            "scope-alpha",
            "owner-alpha",
            "test-resolve-fence"));

    private static ResolveSecretResult MutateFence(
        ResolveSecretResult result,
        string mutation)
    {
        var authority = NyxIdReadAuthorityRef.Parser.ParseFrom(
            Convert.FromBase64String(result.Secret!));
        switch (mutation)
        {
            case "purpose":
                authority.Purpose = "wrong-purpose";
                break;
            case "scope":
                authority.ScopeId = "scope-other";
                break;
            case "owner":
                authority.OwnerSubject = "owner-other";
                break;
            case "request":
                authority.SecretRef = "opaque-authority-other";
                break;
            case "version":
                authority.Version = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return result with
        {
            Secret = Convert.ToBase64String(authority.ToByteArray()),
        };
    }

    private sealed class PurgeableActionAuthorityVault : ISecretVault
    {
        private readonly TimeProvider _clock;
        private ISecretVault _authorityVault;
        private readonly ISecretVault _otherVault;

        public PurgeableActionAuthorityVault(TimeProvider clock)
        {
            _clock = clock;
            _authorityVault = new InMemorySecretVault(clock);
            _otherVault = new InMemorySecretVault(clock);
        }

        public void PurgeAuthorityRecords() =>
            _authorityVault = new InMemorySecretVault(_clock);

        public int AuthorityPutCalls { get; private set; }

        public int AuthorityResolveCalls { get; private set; }

        public bool DeleteAuthorityRecordOnSuccessfulRevoke { get; init; }

        public Func<ResolveSecretResult, ResolveSecretResult>? FenceResolutionTransform
        {
            get;
            set;
        }

        public Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            if (IsAuthorityPurpose(request.Purpose))
                AuthorityPutCalls++;
            return Select(request.Purpose).PutAsync(request, ct);
        }

        public async Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default)
        {
            if (IsAuthorityPurpose(request.Purpose))
                AuthorityResolveCalls++;
            var result = await Select(request.Purpose).ResolveAsync(request, ct);
            return IsFencePurpose(request.Purpose) && FenceResolutionTransform is not null
                ? FenceResolutionTransform(result)
                : result;
        }

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).RotateAsync(request, ct);

        public async Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default)
        {
            var result = await Select(request.Purpose).RevokeAsync(request, ct);
            if (result.Revoked &&
                DeleteAuthorityRecordOnSuccessfulRevoke &&
                IsAuthorityPurpose(request.Purpose))
            {
                _authorityVault = new InMemorySecretVault(_clock);
            }

            return result;
        }

        private ISecretVault Select(string purpose) =>
            IsAuthorityPurpose(purpose)
                ? _authorityVault
                : _otherVault;

        private static bool IsAuthorityPurpose(string purpose) =>
            string.Equals(
                purpose,
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                StringComparison.Ordinal);

        private static bool IsFencePurpose(string purpose) =>
            string.Equals(
                purpose,
                CredentialSecretPurposes.NyxIdChatActionReadAuthorityFence,
                StringComparison.Ordinal);
    }

    private sealed class RevocationRaceActionAuthorityVault : ISecretVault
    {
        private readonly TimeProvider _clock;
        private readonly SecretResolutionFailureReason _pauseOnAuthorityResolution;
        private readonly ISecretVault _otherVault;
        private ISecretVault _authorityVault;
        private readonly TaskCompletionSource<bool> _authorityResolutionPauseReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseAuthorityResolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseAuthorityResolution = 1;
        private int _failFirstAuthorityPut = 1;

        public RevocationRaceActionAuthorityVault(
            TimeProvider clock,
            SecretResolutionFailureReason pauseOnAuthorityResolution)
        {
            _clock = clock;
            _pauseOnAuthorityResolution = pauseOnAuthorityResolution;
            _authorityVault = new InMemorySecretVault(clock);
            _otherVault = new InMemorySecretVault(clock);
        }

        public Task AuthorityResolutionPauseReached =>
            _authorityResolutionPauseReached.Task;

        public bool StoreAuthorityThenFailFirstPut { get; init; }

        public void ReleaseAuthorityResolution() =>
            _releaseAuthorityResolution.TrySetResult(true);

        public async Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            var result = await Select(request.Purpose).PutAsync(request, ct);
            if (StoreAuthorityThenFailFirstPut &&
                IsAuthorityPurpose(request.Purpose) &&
                Interlocked.Exchange(ref _failFirstAuthorityPut, 0) == 1)
            {
                throw new InvalidOperationException("Simulated competing authority creation.");
            }

            return result;
        }

        public async Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default)
        {
            var result = await Select(request.Purpose).ResolveAsync(request, ct);
            if (IsAuthorityPurpose(request.Purpose) &&
                result.FailureReason == _pauseOnAuthorityResolution &&
                Interlocked.Exchange(ref _pauseAuthorityResolution, 0) == 1)
            {
                _authorityResolutionPauseReached.TrySetResult(true);
                await _releaseAuthorityResolution.Task.WaitAsync(ct);
            }

            return result;
        }

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).RotateAsync(request, ct);

        public async Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default)
        {
            var result = await Select(request.Purpose).RevokeAsync(request, ct);
            if (result.Revoked && IsAuthorityPurpose(request.Purpose))
                _authorityVault = new InMemorySecretVault(_clock);
            return result;
        }

        private ISecretVault Select(string purpose) =>
            IsAuthorityPurpose(purpose)
                ? _authorityVault
                : _otherVault;

        private static bool IsAuthorityPurpose(string purpose) =>
            string.Equals(
                purpose,
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                StringComparison.Ordinal);
    }
}
