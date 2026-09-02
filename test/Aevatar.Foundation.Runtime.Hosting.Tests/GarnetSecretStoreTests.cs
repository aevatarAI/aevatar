using System.Text;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetSecretStoreTests
{
    [Fact]
    public async Task GarnetBackedSecretVault_ShouldPersistEncryptedRecordAcrossInstances()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vaultA = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);
        var vaultB = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);
        const string sentinel = "sentinel-vault-secret-value";

        var stored = await vaultA.PutAsync(new StoreSecretRequest(
            Purpose: "oauth-refresh-token",
            OwnerScopeKey: "scope-alpha",
            SubjectId: "user-alpha",
            Secret: sentinel,
            AuditReason: "test store"));

        stored.Reference.Ref.Should().NotBeNullOrWhiteSpace();
        keyValueStore.Values.Should().ContainSingle();
        StoredValueText(keyValueStore).Should().NotContain(sentinel);

        var resolved = await vaultB.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "oauth-refresh-token",
            "scope-alpha",
            "user-alpha",
            "test resolve"));
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be(sentinel);

        var wrongPurpose = await vaultB.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "connector-token",
            "scope-alpha",
            "user-alpha",
            "test wrong purpose"));
        wrongPurpose.Resolved.Should().BeFalse();

        var wrongOwner = await vaultB.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "oauth-refresh-token",
            "scope-beta",
            "user-alpha",
            "test wrong owner"));
        wrongOwner.Resolved.Should().BeFalse();

        var wrongSubject = await vaultB.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "oauth-refresh-token",
            "scope-alpha",
            "user-beta",
            "test wrong subject"));
        wrongSubject.Resolved.Should().BeFalse();
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("garnet")]
    public async Task SecretVaultRequestedReferenceContract_WithNullReference_AllocatesReference(
        string provider)
    {
        var vault = CreateRequestedReferenceContractVault(provider);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test allocated reference",
            RequestedRef: null));

        stored.Reference.Ref.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("in-memory", "")]
    [InlineData("in-memory", " ")]
    [InlineData("garnet", "")]
    [InlineData("garnet", " ")]
    public async Task SecretVaultRequestedReferenceContract_WithBlankReference_RejectsRequest(
        string provider,
        string requestedReference)
    {
        var vault = CreateRequestedReferenceContractVault(provider);

        var put = () => vault.PutAsync(new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test blank reference",
            RequestedRef: requestedReference));

        await put.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*RequestedRef*");
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("garnet")]
    public async Task SecretVaultRequestedReferenceContract_WithExactRetry_ReturnsOriginalReference(
        string provider)
    {
        var vault = CreateRequestedReferenceContractVault(provider);
        var request = new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test exact retry",
            RequestedRef: "sec_requested_contract");

        var first = await vault.PutAsync(request);
        var second = await vault.PutAsync(request);

        second.Reference.Should().BeEquivalentTo(first.Reference);
        second.Reference.Ref.Should().Be("sec_requested_contract");
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("garnet")]
    public async Task SecretVaultRequestedReferenceContract_WithAliasConflict_PreservesOriginalSecret(
        string provider)
    {
        var vault = CreateRequestedReferenceContractVault(provider);
        var original = new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test alias conflict",
            RequestedRef: "sec_requested_contract");
        await vault.PutAsync(original);

        var conflictingPut = () => vault.PutAsync(original with
        {
            SubjectId = "key-beta",
            Secret = "secret-beta",
        });

        await conflictingPut.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            "sec_requested_contract",
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "test original survives"));
        resolved.Secret.Should().Be("secret-alpha");
    }

    [Fact]
    public async Task GarnetBackedSecretVault_WithRequestedReference_RepeatsExactCreateWithoutOverwrite()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var vault = new GarnetBackedSecretVault(
            keyValueStore,
            CreateOptions(),
            CreateKeyring(),
            ManualTime.Start);
        var request = new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test requested reference",
            RequestedRef: "sec_requested_alpha");

        var first = await vault.PutAsync(request);
        var originalBytes = keyValueStore.Values.Should().ContainSingle().Subject.Value.ToArray();
        var second = await vault.PutAsync(request);

        first.Reference.Ref.Should().Be("sec_requested_alpha");
        second.Reference.Should().BeEquivalentTo(first.Reference);
        keyValueStore.Values.Should().ContainSingle().Subject.Value.Should().Equal(originalBytes);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_WithRequestedReference_RejectsAliasWithoutOverwritingOriginal()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var vault = new GarnetBackedSecretVault(
            keyValueStore,
            CreateOptions(),
            CreateKeyring(),
            ManualTime.Start);
        var original = new StoreSecretRequest(
            "scheduled.nyx-api-key",
            "owner-alpha",
            "key-alpha",
            "secret-alpha",
            "test requested reference",
            RequestedRef: "sec_requested_alpha");
        await vault.PutAsync(original);
        var originalBytes = keyValueStore.Values.Should().ContainSingle().Subject.Value.ToArray();

        var conflictingWrite = () => vault.PutAsync(original with
        {
            SubjectId = "key-beta",
            Secret = "secret-beta",
        });

        await conflictingWrite.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        keyValueStore.Values.Should().ContainSingle().Subject.Value.Should().Equal(originalBytes);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldPersistAndEnforceExpiresAt()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var clock = new ManualTime(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, clock);
        var expiresAt = clock.GetUtcNow().AddMinutes(5);
        var expiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "expiring-secret",
            "test store expiring",
            expiresAt));

        stored.Reference.ExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
        keyValueStore.Expirations.Values.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(5));
        var record = GarnetSecretVaultRecord.Parser.ParseFrom(
            keyValueStore.Values.Should().ContainSingle().Subject.Value);
        record.ExpiresAtUnixMs.Should().Be(expiresAtUnixMs);

        var beforeExpiry = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve before expiry"));
        beforeExpiry.Resolved.Should().BeTrue();
        beforeExpiry.Reference!.ExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
        beforeExpiry.Secret.Should().Be("expiring-secret");

        clock.Advance(TimeSpan.FromMinutes(5));
        var expired = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve expired"));
        expired.Resolved.Should().BeFalse();
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldReapplyRemainingExpiryWhenRotating()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var clock = new ManualTime(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, clock);
        var expiresAt = clock.GetUtcNow().AddMinutes(5);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store expiring",
            expiresAt));
        clock.Advance(TimeSpan.FromMinutes(1));

        var rotated = await vault.RotateAsync(new RotateSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "rotated-secret",
            "test rotate expiring"));

        rotated.Reference.ExpiresAtUnixMs.Should().Be(expiresAt.ToUnixTimeMilliseconds());
        keyValueStore.Expirations.Values.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldNotExpandShorterBackendExpiryWhenRotating()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var clock = new ManualTime(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, clock);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store expiring",
            clock.GetUtcNow().AddMinutes(10)));
        var key = keyValueStore.Expirations.Keys.Should().ContainSingle().Which;
        keyValueStore.Expirations[key] = TimeSpan.FromMinutes(2);

        await vault.RotateAsync(new RotateSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "rotated-secret",
            "test rotate expiring"));

        keyValueStore.Expirations.Values.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldRotateByCompareSet()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        var rotated = await vault.RotateAsync(new RotateSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "rotated-secret",
            "test rotate"));

        rotated.Reference.Ref.Should().Be(stored.Reference.Ref);
        rotated.Reference.Version.Should().Be(2);
        rotated.Reference.Fingerprint.Should().NotBe(stored.Reference.Fingerprint);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve rotated"));
        resolved.Secret.Should().Be("rotated-secret");
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldKeepRecordWhenRotateCompareSetLoses()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        var storedEntry = keyValueStore.Values.Should().ContainSingle().Subject;
        var storedBytes = storedEntry.Value.ToArray();
        keyValueStore.RejectNextCompareSet = true;

        var rotate = async () => await vault.RotateAsync(new RotateSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "rotated-secret",
            "test rotate conflict"));

        await rotate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*changed before rotate*");
        keyValueStore.Values.Should().ContainKey(storedEntry.Key)
            .WhoseValue.Should().Equal(storedBytes);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve after rotate conflict"));
        resolved.Secret.Should().Be("initial-secret");
        resolved.Reference!.Version.Should().Be(1);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldRevokeByCompareDelete()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        var revoked = await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test revoke"));

        revoked.Revoked.Should().BeTrue();
        keyValueStore.Values.Should().BeEmpty();

        var replayed = await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test replay revoke"));
        replayed.Revoked.Should().BeTrue("the exact revocation postcondition is already satisfied");

        var afterRevoke = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve after revoke"));
        afterRevoke.Resolved.Should().BeFalse();
        afterRevoke.FailureReason.Should().Be(SecretResolutionFailureReason.NotFound);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldKeepRecordWhenRevokeCompareDeleteLoses()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        var storedEntry = keyValueStore.Values.Should().ContainSingle().Subject;
        var storedBytes = storedEntry.Value.ToArray();
        keyValueStore.RejectNextCompareDelete = true;

        var revoked = await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test revoke conflict"));

        revoked.Revoked.Should().BeFalse();
        keyValueStore.Values.Should().ContainKey(storedEntry.Key)
            .WhoseValue.Should().Equal(storedBytes);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve after revoke conflict"));
        resolved.Secret.Should().Be("initial-secret");
    }

    [Fact]
    public async Task GarnetBackedSecretVault_WhenCompetingRevokeDeletesFirst_ReturnsSuccess()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var vault = new GarnetBackedSecretVault(
            keyValueStore,
            CreateOptions(),
            CreateKeyring(),
            ManualTime.Start);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        keyValueStore.RejectNextCompareDelete = true;
        keyValueStore.DeleteOnNextRejectedCompareDelete = true;

        var revoked = await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test concurrent revoke"));

        revoked.Revoked.Should().BeTrue();
        keyValueStore.Values.Should().BeEmpty();
    }

    [GarnetIntegrationFact]
    public async Task GarnetSecretKeyValueStore_ShouldCompareSetAndCompareDeleteWithExactBytes()
    {
        var options = CreateOptions();
        options.ConnectionString = RequireGarnetConnectionString();
        using var connection = new GarnetSecretConnectionMultiplexer(options);
        var store = new GarnetSecretKeyValueStore(connection, options);
        var key = $"{options.SecretVaultPrefix}:compare:{Guid.NewGuid():N}";
        byte[] original = [0x00, 0x01, 0xFE, 0xFF];
        byte[] wrongExpected = [0x00, 0x01, 0xFE, 0x00];
        byte[] updated = [0x10, 0x20, 0x30, 0x40];
        var createOnlyKey = $"{options.SecretVaultPrefix}:set-nx:{Guid.NewGuid():N}";
        byte[] createOnlyOriginal = [0xA1, 0xB2, 0xC3];
        byte[] createOnlyConflict = [0xD4, 0xE5, 0xF6];

        var firstCreate = await store.SetIfAbsentAsync(createOnlyKey, createOnlyOriginal, null);
        var conflictingCreate = await store.SetIfAbsentAsync(createOnlyKey, createOnlyConflict, null);

        firstCreate.Should().BeTrue();
        conflictingCreate.Should().BeFalse();
        (await store.GetAsync(createOnlyKey)).Should().Equal(createOnlyOriginal);

        await store.SetAsync(key, original, TimeSpan.FromMinutes(2));

        var rejectedSet = await store.CompareSetAsync(
            key,
            wrongExpected,
            updated,
            TimeSpan.FromMinutes(4));

        rejectedSet.Should().BeFalse();
        (await store.GetAsync(key)).Should().Equal(original);

        var acceptedSet = await store.CompareSetAsync(
            key,
            original,
            updated,
            TimeSpan.FromMinutes(4));

        acceptedSet.Should().BeTrue();
        (await store.GetAsync(key)).Should().Equal(updated);
        var ttl = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull();
        ttl.Should().BeGreaterThan(TimeSpan.FromMinutes(1));
        ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(2));

        var rejectedDelete = await store.CompareDeleteAsync(key, original);

        rejectedDelete.Should().BeFalse();
        (await store.GetAsync(key)).Should().Equal(updated);

        var acceptedDelete = await store.CompareDeleteAsync(key, updated);

        acceptedDelete.Should().BeTrue();
        (await store.GetAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldRejectNewlineDelimitedAadCollisionAfterRecordTampering()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "oauth-refresh-token\nscope-alpha",
            "subject-alpha",
            "tenant-alpha",
            "collision-secret",
            "test store collision"));
        var storedEntry = keyValueStore.Values.Should().ContainSingle().Subject;
        var tampered = GarnetSecretVaultRecord.Parser.ParseFrom(storedEntry.Value);
        tampered.Purpose = "oauth-refresh-token";
        tampered.OwnerScopeKey = "scope-alpha\nsubject-alpha";
        tampered.SubjectId = "tenant-alpha";
        keyValueStore.Values[storedEntry.Key] = tampered.ToByteArray();

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "oauth-refresh-token",
            "scope-alpha\nsubject-alpha",
            "tenant-alpha",
            "test resolve tampered collision"));

        resolved.Resolved.Should().BeFalse();
        resolved.Secret.Should().BeNull();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.AuthenticationFailed);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldReturnKeyringMismatch_WhenCiphertextKeyIsMissing()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var writerKeyring = CreateKeyring();
        var readerKeyring = CreateKeyringWithSingleKey(seed: 9, fingerprintSeed: 3);
        var writer = new GarnetBackedSecretVault(keyValueStore, options, writerKeyring, ManualTime.Start);
        var reader = new GarnetBackedSecretVault(keyValueStore, options, readerKeyring, ManualTime.Start);

        var stored = await writer.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));

        var resolved = await reader.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve with drifted keyring"));

        resolved.Resolved.Should().BeFalse();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.KeyringMismatch);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldReturnInvalidRecord_WhenStoredBytesAreNotProtobuf()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);
        const string reference = "sec_corrupt";
        keyValueStore.Values[$"{options.SecretVaultPrefix}:{reference}"] = [0x80];

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            reference,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve corrupt record"));

        resolved.Resolved.Should().BeFalse();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.InvalidRecord);
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldReturnUnsupportedAlgorithm_WhenCiphertextAlgorithmIsUnknown()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var vault = new GarnetBackedSecretVault(keyValueStore, options, keyring, ManualTime.Start);

        var stored = await vault.PutAsync(new StoreSecretRequest(
            "api-token",
            "scope-alpha",
            "user-alpha",
            "initial-secret",
            "test store"));
        var storedEntry = keyValueStore.Values.Should().ContainSingle().Subject;
        var record = GarnetSecretVaultRecord.Parser.ParseFrom(storedEntry.Value);
        record.EncryptedSecret.Algorithm = "AES-128-GCM";
        keyValueStore.Values[storedEntry.Key] = record.ToByteArray();

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve unsupported algorithm"));

        resolved.Resolved.Should().BeFalse();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.UnsupportedAlgorithm);
    }

    [Fact]
    public async Task GarnetRuntimeSecretStore_ShouldPersistEncryptedRecordWithTtlAndResolve()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var clock = new ManualTime(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var storeA = new GarnetRuntimeSecretStore(keyValueStore, options, keyring, clock);
        var storeB = new GarnetRuntimeSecretStore(keyValueStore, options, keyring, clock);
        const string sentinel = "sentinel-runtime-secret-value";

        var stored = await storeA.PutAsync(new StoreRuntimeSecretRequest(
            Purpose: "workflow-tool-token",
            OwnerRunId: "run-alpha",
            OwnerStepId: "step-alpha",
            Secret: sentinel,
            TimeToLive: TimeSpan.FromMinutes(5),
            ConsumeOnce: false,
            AuditReason: "test store runtime"));

        keyValueStore.Values.Should().ContainSingle();
        StoredValueText(keyValueStore).Should().NotContain(sentinel);
        keyValueStore.Expirations.Values.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMinutes(5));

        var resolved = await storeB.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test resolve runtime"));
        resolved.Secret.Should().Be(sentinel);

        var wrongStep = await storeB.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-beta",
            "test wrong step"));
        wrongStep.Resolved.Should().BeFalse();

        var secondResolve = await storeB.ResolveAsync(new ResolveRuntimeSecretRequest(
            stored.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test second resolve"));
        secondResolve.Secret.Should().Be(sentinel);
    }

    [Fact]
    public async Task GarnetRuntimeSecretStore_ShouldRejectConsumeOnceBeforeWritingRecord()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var store = new GarnetRuntimeSecretStore(keyValueStore, options, keyring, ManualTime.Start);
        const string sentinel = "sentinel-runtime-secret-value";

        var put = async () => await store.PutAsync(new StoreRuntimeSecretRequest(
            Purpose: "workflow-tool-token",
            OwnerRunId: "run-alpha",
            OwnerStepId: "step-alpha",
            Secret: sentinel,
            TimeToLive: TimeSpan.FromMinutes(5),
            ConsumeOnce: true,
            AuditReason: "test store runtime consume once"));

        await put.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*consume-once requires atomic consume support*");
        keyValueStore.Values.Should().BeEmpty();
        keyValueStore.Expirations.Should().BeEmpty();
    }

    [Fact]
    public async Task GarnetRuntimeSecretStore_ShouldRejectConsumeBeforeReadingOrDecryptingRecord()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var store = new GarnetRuntimeSecretStore(keyValueStore, options, keyring, ManualTime.Start);

        var consume = async () => await store.ConsumeAsync(new ConsumeRuntimeSecretRequest(
            Ref: "rsec_missing",
            Purpose: "workflow-tool-token",
            OwnerRunId: "run-alpha",
            OwnerStepId: "step-alpha",
            AuditReason: "test consume runtime"));

        await consume.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*consume-once requires atomic consume support*");
        keyValueStore.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task GarnetRuntimeSecretStore_ShouldFailClosedWhenExpiredOrRevoked()
    {
        var keyValueStore = new RecordingGarnetSecretKeyValueStore();
        var options = CreateOptions();
        var keyring = CreateKeyring();
        var clock = new ManualTime(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
        var store = new GarnetRuntimeSecretStore(keyValueStore, options, keyring, clock);

        var expiring = await store.PutAsync(new StoreRuntimeSecretRequest(
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "expiring-secret",
            TimeSpan.FromSeconds(30),
            ConsumeOnce: false,
            "test store expiring"));

        clock.Advance(TimeSpan.FromSeconds(31));
        var expired = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            expiring.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test expired"));
        expired.Resolved.Should().BeFalse();

        var revocable = await store.PutAsync(new StoreRuntimeSecretRequest(
            "workflow-tool-token",
            "run-alpha",
            "step-beta",
            "revoked-secret",
            TimeSpan.FromMinutes(10),
            ConsumeOnce: false,
            "test store revocable"));

        var revoked = await store.RevokeAsync(new RevokeRuntimeSecretRequest(
            revocable.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-beta",
            "test revoke"));
        revoked.Revoked.Should().BeTrue();

        var afterRevoke = await store.ResolveAsync(new ResolveRuntimeSecretRequest(
            revocable.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-beta",
            "test resolve revoked"));
        afterRevoke.Resolved.Should().BeFalse();
    }

    [Fact]
    public void GarnetSecretStoreKeyring_ShouldRejectMissingMalformedNon32ByteAndMissingFingerprintKeys()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-keyring-{Guid.NewGuid():N}.json");
        var missing = () => GarnetSecretStoreKeyring.LoadFromFile(missingPath);
        missing.Should().Throw<InvalidOperationException>().WithMessage("*keyring*");

        using var malformed = new TempFile("{");
        var malformedLoad = () => GarnetSecretStoreKeyring.LoadFromFile(malformed.Path);
        malformedLoad.Should().Throw<InvalidOperationException>().WithMessage("*malformed*");

        using var missingActive = new TempFile($$"""
        {
          "activeKeyId": "missing",
          "keys": {
            "key-1": "{{Base64Key(1)}}"
          }
        }
        """);
        var missingActiveLoad = () => GarnetSecretStoreKeyring.LoadFromFile(missingActive.Path);
        missingActiveLoad.Should().Throw<InvalidOperationException>().WithMessage("*activeKeyId*");

        using var shortKey = new TempFile("""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "AQID"
          }
        }
        """);
        var shortKeyLoad = () => GarnetSecretStoreKeyring.LoadFromFile(shortKey.Path);
        shortKeyLoad.Should().Throw<InvalidOperationException>().WithMessage("*32-byte*");

        using var missingFingerprint = new TempFile($$"""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "{{Base64Key(1)}}"
          }
        }
        """);
        var missingFingerprintLoad = () => GarnetSecretStoreKeyring.LoadFromFile(missingFingerprint.Path);
        missingFingerprintLoad.Should().Throw<InvalidOperationException>().WithMessage("*fingerprintKey*");

        using var valid = new TempFile($$"""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "{{Base64Key(1)}}"
          },
          "fingerprintKey": "{{Base64Key(2)}}"
        }
        """);
        var keyring = GarnetSecretStoreKeyring.LoadFromFile(valid.Path);
        keyring.ActiveKeyId.Should().Be("key-1");
    }

    [Fact]
    public void GarnetSecretStoreOptions_ShouldRejectInvalidOrSharedPrefixes()
    {
        var emptyVaultPrefix = () => new GarnetSecretStoreOptions
        {
            KeyringPath = "/tmp/keyring.json",
            SecretVaultPrefix = " ",
            RuntimeSecretPrefix = "aevatar:runtime-secrets",
        }.Validate();
        emptyVaultPrefix.Should().Throw<InvalidOperationException>().WithMessage("*SecretVaultPrefix*");

        var sharedPrefixes = () => new GarnetSecretStoreOptions
        {
            KeyringPath = "/tmp/keyring.json",
            SecretVaultPrefix = "aevatar:secrets",
            RuntimeSecretPrefix = "aevatar:secrets",
        }.Validate();
        sharedPrefixes.Should().Throw<InvalidOperationException>().WithMessage("*distinct*");
    }

    [Fact]
    public void AddGarnetSecretStores_ShouldRegisterVaultAndRuntimeStore()
    {
        using var keyringFile = new TempFile($$"""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "{{Base64Key(1)}}"
          },
          "fingerprintKey": "{{Base64Key(2)}}"
        }
        """);
        var services = new ServiceCollection();
        services.AddSingleton<IGarnetSecretKeyValueStore, RecordingGarnetSecretKeyValueStore>();

        services.AddGarnetSecretStores(options =>
        {
            options.ConnectionString = "localhost:6379,abortConnect=false";
            options.KeyringPath = keyringFile.Path;
            options.SecretVaultPrefix = "aevatar:test:vault";
            options.RuntimeSecretPrefix = "aevatar:test:runtime";
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISecretVault>().Should().BeOfType<GarnetBackedSecretVault>();
        provider.GetRequiredService<IRuntimeSecretStore>().Should().BeOfType<GarnetRuntimeSecretStore>();
    }

    [Fact]
    public void AddGarnetSecretStores_ShouldUseSecretSpecificConnectionWhenEventStoreMultiplexerExists()
    {
        using var keyringFile = new TempFile($$"""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "{{Base64Key(1)}}"
          },
          "fingerprintKey": "{{Base64Key(2)}}"
        }
        """);
        var services = new ServiceCollection();

        services.AddGarnetEventStore(options =>
        {
            options.ConnectionString = "localhost:6379,abortConnect=false,name=events";
            options.KeyPrefix = "aevatar:test:events";
        });
        services.AddGarnetSecretStores(options =>
        {
            options.ConnectionString = "localhost:6380,abortConnect=false,name=secrets";
            options.KeyringPath = keyringFile.Path;
            options.SecretVaultPrefix = "aevatar:test:vault";
            options.RuntimeSecretPrefix = "aevatar:test:runtime";
        });

        services.Where(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer))
            .Should()
            .ContainSingle();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType.Name.Contains("GarnetSecretConnection", StringComparison.Ordinal));
        typeof(GarnetSecretKeyValueStore)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Which.GetParameters()
            .Should()
            .NotContain(parameter => parameter.ParameterType == typeof(IConnectionMultiplexer));
    }

    private static GarnetSecretStoreOptions CreateOptions() => new()
    {
        KeyringPath = "/unused/keyring.json",
        SecretVaultPrefix = $"aevatar:test:vault:{Guid.NewGuid():N}",
        RuntimeSecretPrefix = $"aevatar:test:runtime:{Guid.NewGuid():N}",
    };

    private static ISecretVault CreateRequestedReferenceContractVault(string provider) =>
        provider switch
        {
            "in-memory" => new InMemorySecretVault(),
            "garnet" => new GarnetBackedSecretVault(
                new RecordingGarnetSecretKeyValueStore(),
                CreateOptions(),
                CreateKeyring(),
                ManualTime.Start),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

    private static GarnetSecretStoreKeyring CreateKeyring()
    {
        using var keyringFile = new TempFile($$"""
        {
          "activeKeyId": "key-1",
          "keys": {
            "key-1": "{{Base64Key(1)}}",
            "key-2": "{{Base64Key(2)}}"
          },
          "fingerprintKey": "{{Base64Key(3)}}"
        }
        """);
        return GarnetSecretStoreKeyring.LoadFromFile(keyringFile.Path);
    }

    private static GarnetSecretStoreKeyring CreateKeyringWithSingleKey(byte seed, byte fingerprintSeed)
    {
        using var keyringFile = new TempFile($$"""
        {
          "activeKeyId": "key-9",
          "keys": {
            "key-9": "{{Base64Key(seed)}}"
          },
          "fingerprintKey": "{{Base64Key(fingerprintSeed)}}"
        }
        """);
        return GarnetSecretStoreKeyring.LoadFromFile(keyringFile.Path);
    }

    private static string StoredValueText(RecordingGarnetSecretKeyValueStore keyValueStore)
    {
        var bytes = keyValueStore.Values.Should().ContainSingle().Subject.Value;
        return Encoding.UTF8.GetString(bytes);
    }

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    private static string Base64Key(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return Convert.ToBase64String(bytes);
    }

    private sealed class RecordingGarnetSecretKeyValueStore : IGarnetSecretKeyValueStore
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan?> Expirations { get; } = new(StringComparer.Ordinal);

        public bool RejectNextCompareSet { get; set; }

        public bool RejectNextCompareDelete { get; set; }

        public bool DeleteOnNextRejectedCompareDelete { get; set; }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Values[key] = value.ToArray();
            Expirations[key] = expiry;
            return Task.CompletedTask;
        }

        public Task<bool> SetIfAbsentAsync(
            string key,
            ReadOnlyMemory<byte> value,
            TimeSpan? expiry,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Values.ContainsKey(key))
                return Task.FromResult(false);

            Values[key] = value.ToArray();
            Expirations[key] = expiry;
            return Task.FromResult(true);
        }

        public Task<bool> CompareSetAsync(
            string key,
            ReadOnlyMemory<byte> expectedValue,
            ReadOnlyMemory<byte> newValue,
            TimeSpan? expiry,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (RejectNextCompareSet)
            {
                RejectNextCompareSet = false;
                return Task.FromResult(false);
            }

            if (!Values.TryGetValue(key, out var current) ||
                !current.AsSpan().SequenceEqual(expectedValue.Span))
            {
                return Task.FromResult(false);
            }

            Values[key] = newValue.ToArray();
            Expirations.TryGetValue(key, out var existingExpiry);
            Expirations[key] = existingExpiry.HasValue &&
                               (!expiry.HasValue || existingExpiry.Value < expiry.Value)
                ? existingExpiry
                : expiry;
            return Task.FromResult(true);
        }

        public Task<bool> CompareDeleteAsync(
            string key,
            ReadOnlyMemory<byte> expectedValue,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (RejectNextCompareDelete)
            {
                RejectNextCompareDelete = false;
                if (DeleteOnNextRejectedCompareDelete)
                {
                    DeleteOnNextRejectedCompareDelete = false;
                    Values.Remove(key);
                    Expirations.Remove(key);
                }
                return Task.FromResult(false);
            }

            if (!Values.TryGetValue(key, out var current) ||
                !current.AsSpan().SequenceEqual(expectedValue.Span))
            {
                return Task.FromResult(false);
            }

            Values.Remove(key);
            Expirations.Remove(key);
            return Task.FromResult(true);
        }
    }

    private sealed class ManualTime(DateTimeOffset utcNow) : TimeProvider
    {
        public static ManualTime Start => new(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));

        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-keyring-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
