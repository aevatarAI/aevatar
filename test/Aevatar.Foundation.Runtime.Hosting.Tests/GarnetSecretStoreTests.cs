using System.Text;
using Aevatar.Foundation.Abstractions.Credentials;
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
    public async Task GarnetBackedSecretVault_ShouldRejectRotateBeforeModifyingRecord()
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
        var storedBytes = keyValueStore.Values.Should().ContainSingle().Subject.Value.ToArray();

        var rotate = async () => await vault.RotateAsync(new RotateSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "rotated-secret",
            "test rotate"));

        await rotate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*atomic versioned transitions*");
        keyValueStore.Values.Should().ContainSingle().Which.Value.Should().Equal(storedBytes);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve original after rejected rotate"));
        resolved.Secret.Should().Be("initial-secret");
    }

    [Fact]
    public async Task GarnetBackedSecretVault_ShouldRejectRevokeBeforeModifyingRecord()
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
        var storedBytes = keyValueStore.Values.Should().ContainSingle().Subject.Value.ToArray();

        var revoke = async () => await vault.RevokeAsync(new RevokeSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test revoke"));

        await revoke.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*atomic versioned transitions*");
        keyValueStore.Values.Should().ContainSingle().Which.Value.Should().Equal(storedBytes);

        var afterRevoke = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            "api-token",
            "scope-alpha",
            "user-alpha",
            "test resolve original after rejected revoke"));
        afterRevoke.Secret.Should().Be("initial-secret");
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

    private static string StoredValueText(RecordingGarnetSecretKeyValueStore keyValueStore)
    {
        var bytes = keyValueStore.Values.Should().ContainSingle().Subject.Value;
        return Encoding.UTF8.GetString(bytes);
    }

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
