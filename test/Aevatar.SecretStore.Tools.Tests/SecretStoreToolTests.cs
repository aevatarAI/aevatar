using System.Text;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.SecretStore.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.SecretStore.Tools.Tests;

public sealed class SecretStoreToolTests
{
    [Fact]
    public void CommandNames_ShouldExposeOnlyAuthorizedCommands()
    {
        SecretStoreTool.CommandNames.Should().Equal("generate-keyring", "add-key", "reencrypt-sweep");
        SecretStoreTool.CommandNames.Should().NotContain("remove-key");
    }

    [Fact]
    public async Task GenerateKeyringAndAddKey_ShouldWriteCanonicalSchemaAndPreserveFingerprintKey()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "keyring.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var generated = await SecretStoreTool.MainAsync(
            ["generate-keyring", "--output", path, "--active-key-id", "key-a"],
            output,
            error);

        generated.Should().Be(0);
        var generatedJson = File.ReadAllText(path);
        generatedJson.Should().Contain("\"activeKeyId\"");
        generatedJson.Should().Contain("\"keys\"");
        generatedJson.Should().Contain("\"fingerprintKey\"");
        generatedJson.Should().NotContain("fingerprintKeyBase64");

        var document = SecretStoreKeyringDocument.LoadFromFile(path);
        document.ActiveKeyId.Should().Be("key-a");
        document.Keys.Should().ContainKey("key-a");
        document.FingerprintKey.Should().NotBe(document.Keys!["key-a"]);
        var initialFingerprintKey = document.FingerprintKey;
        var loadGenerated = () => GarnetSecretStoreKeyring.LoadFromFile(path);
        loadGenerated.Should().NotThrow();

        var added = await SecretStoreTool.MainAsync(
            ["add-key", "--keyring", path, "--key-id", "key-b"],
            output,
            error);

        added.Should().Be(0);
        var updatedDocument = SecretStoreKeyringDocument.LoadFromFile(path);
        updatedDocument.ActiveKeyId.Should().Be("key-b");
        updatedDocument.Keys.Should().ContainKeys("key-a", "key-b");
        updatedDocument.FingerprintKey.Should().Be(initialFingerprintKey);
    }

    [Fact]
    public async Task ReencryptSweep_DryRunShouldVerifyWithoutWriting()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SecretStoreTool.MainAsync(
            [
                "reencrypt-sweep",
                "--keyring",
                fixture.NewKeyringPath,
                "--secret-vault-prefix",
                fixture.Options.SecretVaultPrefix,
                "--runtime-secret-prefix",
                fixture.Options.RuntimeSecretPrefix,
                "--dry-run",
                "--verify",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        fixture.Target.CompareExchangeCalls.Should().Be(0);
        output.ToString().Should().Contain("verified=1").And.Contain("verifyFailures=0");
        var record = GarnetRuntimeSecretRecord.Parser.ParseFrom(fixture.Target.Values[fixture.StoredKey]);
        record.EncryptedSecret.KeyId.Should().Be("old");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldCasWritePreserveTtlCheckpointAndVerify()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        using var checkpoint = new TempFile();
        var checkpointPath = checkpoint.Path;
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SecretStoreTool.MainAsync(
            [
                "reencrypt-sweep",
                "--keyring",
                fixture.NewKeyringPath,
                "--secret-vault-prefix",
                fixture.Options.SecretVaultPrefix,
                "--runtime-secret-prefix",
                fixture.Options.RuntimeSecretPrefix,
                "--checkpoint",
                checkpointPath,
                "--verify",
                "--batch-size",
                "1",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        fixture.Target.CompareExchangeCalls.Should().Be(1);
        fixture.Target.Ttls[fixture.StoredKey].Should().Be(TimeSpan.FromMinutes(10));
        File.ReadAllText(checkpointPath).Should().Contain("\"phase\": \"done\"");

        var record = GarnetRuntimeSecretRecord.Parser.ParseFrom(fixture.Target.Values[fixture.StoredKey]);
        record.EncryptedSecret.KeyId.Should().Be("new");

        var store = new RecordingGarnetSecretKeyValueStore();
        store.Values[fixture.StoredKey] = fixture.Target.Values[fixture.StoredKey].ToArray();
        store.Expirations[fixture.StoredKey] = fixture.Target.Ttls[fixture.StoredKey];
        var runtimeStore = new GarnetRuntimeSecretStore(
            store,
            fixture.Options,
            GarnetSecretStoreKeyring.LoadFromFile(fixture.NewKeyringPath));

        var resolved = await runtimeStore.ResolveAsync(new ResolveRuntimeSecretRequest(
            fixture.Reference.Ref,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test resolve after reencrypt"));

        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("runtime-secret");
    }

    private static string Base64Key(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i);

        return Convert.ToBase64String(bytes);
    }

    private sealed class ReencryptionFixture : IDisposable
    {
        private readonly TempFile _oldKeyring;
        private readonly TempFile _newKeyring;

        private ReencryptionFixture(
            TempFile oldKeyring,
            TempFile newKeyring,
            GarnetSecretStoreOptions options,
            FakeSweepTarget target,
            string storedKey,
            RuntimeSecretReference reference)
        {
            _oldKeyring = oldKeyring;
            _newKeyring = newKeyring;
            Options = options;
            Target = target;
            StoredKey = storedKey;
            Reference = reference;
        }

        public string NewKeyringPath => _newKeyring.Path;

        public GarnetSecretStoreOptions Options { get; }

        public FakeSweepTarget Target { get; }

        public string StoredKey { get; }

        public RuntimeSecretReference Reference { get; }

        public static async Task<ReencryptionFixture> CreateAsync()
        {
            var oldKeyring = new TempFile($$"""
            {
              "activeKeyId": "old",
              "keys": {
                "old": "{{Base64Key(1)}}"
              },
              "fingerprintKey": "{{Base64Key(21)}}"
            }
            """);
            var newKeyring = new TempFile($$"""
            {
              "activeKeyId": "new",
              "keys": {
                "old": "{{Base64Key(1)}}",
                "new": "{{Base64Key(51)}}"
              },
              "fingerprintKey": "{{Base64Key(21)}}"
            }
            """);
            var options = new GarnetSecretStoreOptions
            {
                KeyringPath = oldKeyring.Path,
                SecretVaultPrefix = $"aevatar:test:vault:{Guid.NewGuid():N}",
                RuntimeSecretPrefix = $"aevatar:test:runtime:{Guid.NewGuid():N}",
            };
            var recordingStore = new RecordingGarnetSecretKeyValueStore();
            var runtimeStore = new GarnetRuntimeSecretStore(
                recordingStore,
                options,
                GarnetSecretStoreKeyring.LoadFromFile(oldKeyring.Path));

            var stored = await runtimeStore.PutAsync(new StoreRuntimeSecretRequest(
                "workflow-tool-token",
                "run-alpha",
                "step-alpha",
                "runtime-secret",
                TimeSpan.FromMinutes(10),
                ConsumeOnce: false,
                "test store"));
            var entry = recordingStore.Values.Should().ContainSingle().Subject;
            var target = new FakeSweepTarget();
            target.Values[entry.Key] = entry.Value.ToArray();
            target.Ttls[entry.Key] = recordingStore.Expirations[entry.Key];

            return new ReencryptionFixture(oldKeyring, newKeyring, options, target, entry.Key, stored.Reference);
        }

        public void Dispose()
        {
            _oldKeyring.Dispose();
            _newKeyring.Dispose();
        }
    }

    private sealed class FakeSweepTarget : ISecretStoreSweepTarget
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan?> Ttls { get; } = new(StringComparer.Ordinal);

        public int CompareExchangeCalls { get; private set; }

        public Task<SecretStoreScanBatch> ScanAsync(
            string pattern,
            long cursor,
            int count,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;
            var keys = Values.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var start = (int)cursor;
            var batch = keys.Skip(start).Take(count).ToArray();
            var nextCursor = start + batch.Length >= keys.Length ? 0 : start + batch.Length;
            return Task.FromResult(new SecretStoreScanBatch(nextCursor, batch));
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task<SecretStoreCasResult> CompareExchangeAsync(
            string key,
            byte[] expectedValue,
            byte[] newValue,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CompareExchangeCalls++;
            if (!Values.TryGetValue(key, out var current))
                return Task.FromResult(SecretStoreCasResult.Missing());
            if (!current.SequenceEqual(expectedValue))
                return Task.FromResult(SecretStoreCasResult.Conflict());

            Values[key] = newValue.ToArray();
            var ttl = Ttls[key];
            return Task.FromResult(SecretStoreCasResult.Updated(ttl.HasValue ? (long)ttl.Value.TotalMilliseconds : -1));
        }
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-secret-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-secret-tool-{Guid.NewGuid():N}.json");
        }

        public TempFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-secret-tool-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, contents, Encoding.UTF8);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
