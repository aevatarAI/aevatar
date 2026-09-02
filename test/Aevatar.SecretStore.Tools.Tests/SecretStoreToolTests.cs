using System.Text;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.SecretStore.Tools;
using FluentAssertions;
using StackExchange.Redis;
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
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
    public void LoadKeyring_ShouldRepairOverPermissiveUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "keyring.json");
        SecretStoreKeyringDocument.Create("key-a").SaveToFile(path, overwrite: false);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var loaded = SecretStoreKeyringDocument.LoadFromFile(path);

        loaded.ActiveKeyId.Should().Be("key-a");
        File.GetUnixFileMode(path).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
            fixture.ReferenceRef,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test resolve after reencrypt"));

        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("runtime-secret");
    }

    [Fact]
    public async Task ReencryptSweep_ResumeDoneCheckpointShouldReturnStoredResultWithoutScanning()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        using var checkpoint = new TempFile("""
        {
          "phase": "done",
          "cursor": 0,
          "result": {
            "scanned": 7,
            "changed": 3,
            "updated": 2,
            "alreadyActive": 1,
            "conflicts": 0,
            "missing": 0,
            "errors": 0,
            "verified": 4,
            "verifyFailures": 0
          }
        }
        """);
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
                checkpoint.Path,
                "--resume",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        fixture.Target.ScanRequests.Should().BeEmpty();
        output.ToString()
            .Should()
            .Contain("scanned=7")
            .And.Contain("changed=3")
            .And.Contain("updated=2")
            .And.Contain("alreadyActive=1")
            .And.Contain("verified=4");
    }

    [Fact]
    public async Task ReencryptSweep_ResumeMidRunCheckpointShouldContinueFromRecordedPhaseCursor()
    {
        using var fixture = await ReencryptionFixture.CreateEmptyAsync();
        await fixture.AddRuntimeSecretAsync("step-1", "runtime-secret-1");
        await fixture.AddRuntimeSecretAsync("step-2", "runtime-secret-2");
        await fixture.AddRuntimeSecretAsync("step-3", "runtime-secret-3");
        var orderedRuntimeKeys = fixture.Target.Values.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        orderedRuntimeKeys.Should().HaveCount(3);
        using var checkpoint = new TempFile("""
        {
          "phase": "runtime",
          "cursor": 1,
          "result": {
            "scanned": 5,
            "changed": 2,
            "updated": 2,
            "alreadyActive": 0,
            "conflicts": 0,
            "missing": 0,
            "errors": 0,
            "verified": 0,
            "verifyFailures": 0
          }
        }
        """);
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
                checkpoint.Path,
                "--resume",
                "--batch-size",
                "1",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        fixture.Target.ScanRequests
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(request => request.Pattern.StartsWith(fixture.Options.RuntimeSecretPrefix, StringComparison.Ordinal));
        fixture.Target.ScanRequests[0].Cursor.Should().Be(1);
        File.ReadAllText(checkpoint.Path).Should().Contain("\"phase\": \"done\"");
        output.ToString().Should().Contain("scanned=7").And.Contain("changed=4").And.Contain("updated=4");
        RuntimeRecordKeyId(fixture.Target.Values[orderedRuntimeKeys[0]]).Should().Be("old");
        RuntimeRecordKeyId(fixture.Target.Values[orderedRuntimeKeys[1]]).Should().Be("new");
        RuntimeRecordKeyId(fixture.Target.Values[orderedRuntimeKeys[2]]).Should().Be("new");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReencryptSecretVaultRecordsAndResolveWithNewKey()
    {
        using var fixture = await ReencryptionFixture.CreateVaultAsync();
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
                "--verify",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        output.ToString().Should().Contain("changed=1").And.Contain("updated=1").And.Contain("verifyFailures=0");
        var record = GarnetSecretVaultRecord.Parser.ParseFrom(fixture.Target.Values[fixture.StoredKey]);
        record.EncryptedSecret.KeyId.Should().Be("new");

        var store = new RecordingGarnetSecretKeyValueStore();
        store.Values[fixture.StoredKey] = fixture.Target.Values[fixture.StoredKey].ToArray();
        store.Expirations[fixture.StoredKey] = fixture.Target.Ttls[fixture.StoredKey];
        var vault = new GarnetBackedSecretVault(
            store,
            fixture.Options,
            GarnetSecretStoreKeyring.LoadFromFile(fixture.NewKeyringPath),
            TimeProvider.System);

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            fixture.ReferenceRef,
            "oauth-refresh-token",
            "scope-alpha",
            "user-alpha",
            "test resolve vault after reencrypt"));

        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("vault-secret");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReportAlreadyActiveWithoutCasWrite()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SecretStoreTool.MainAsync(
            [
                "reencrypt-sweep",
                "--keyring",
                fixture.OldKeyringPath,
                "--secret-vault-prefix",
                fixture.Options.SecretVaultPrefix,
                "--runtime-secret-prefix",
                fixture.Options.RuntimeSecretPrefix,
                "--verify",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(0);
        fixture.Target.CompareExchangeCalls.Should().Be(0);
        output.ToString().Should().Contain("alreadyActive=1").And.Contain("verified=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenScannedRecordIsMissing()
    {
        using var fixture = await ReencryptionFixture.CreateEmptyAsync();
        fixture.Target.AdditionalScanKeys.Add($"{fixture.Options.RuntimeSecretPrefix}:missing");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await RunInjectedSweepAsync(fixture, output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("missing=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenRecordCannotBeDecrypted()
    {
        using var fixture = await ReencryptionFixture.CreateEmptyAsync();
        var key = $"{fixture.Options.RuntimeSecretPrefix}:malformed";
        fixture.Target.Values[key] = Encoding.UTF8.GetBytes("not a protobuf secret record");
        fixture.Target.Ttls[key] = null;
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await RunInjectedSweepAsync(fixture, output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("errors=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenCasConflicts()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        fixture.Target.NextCasResult = SecretStoreCasResult.Conflict();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await RunInjectedSweepAsync(fixture, output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("conflicts=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenCasReportsMissing()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        fixture.Target.NextCasResult = SecretStoreCasResult.Missing();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await RunInjectedSweepAsync(fixture, output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("missing=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenCasStatusIsUnknown()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        fixture.Target.NextCasResult = new SecretStoreCasResult((SecretStoreCasStatus)999, -1);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await RunInjectedSweepAsync(fixture, output, error);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("errors=1");
    }

    [Fact]
    public async Task ReencryptSweep_ShouldReturnExitCodeTwoWhenVerifyFindsOldStoredKey()
    {
        using var fixture = await ReencryptionFixture.CreateAsync();
        fixture.Target.StoreExpectedValueAfterUpdated = true;
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
                "--verify",
            ],
            output,
            error,
            fixture.Target);

        exitCode.Should().Be(2);
        output.ToString().Should().Contain("updated=1").And.Contain("verifyFailures=1");
    }

    [GarnetIntegrationFact]
    public async Task RedisSecretStoreSweepTarget_ShouldScanGetCompareExchangeAndPreserveTtlAgainstGarnet()
    {
        var connectionString = RequireGarnetConnectionString();
        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        const int configuredDatabase = 1;
        var database = connection.GetDatabase(configuredDatabase);
        var defaultDatabase = connection.GetDatabase(0);
        var prefix = $"aevatar:test:secret-sweep:{Guid.NewGuid():N}";
        var key = $"{prefix}:record";
        var defaultDatabaseKey = $"{prefix}:default-database-record";
        var missingKey = $"{prefix}:missing";
        var originalValue = Encoding.UTF8.GetBytes("old-record");
        var updatedValue = Encoding.UTF8.GetBytes("new-record");
        var wrongExpectedValue = Encoding.UTF8.GetBytes("wrong-record");
        var defaultDatabaseSameKeyValue = Encoding.UTF8.GetBytes("db-zero-record");

        (await database.StringSetAsync(key, originalValue, TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await defaultDatabase.StringSetAsync(defaultDatabaseKey, originalValue, TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await defaultDatabase.StringSetAsync(key, defaultDatabaseSameKeyValue, TimeSpan.FromMinutes(5))).Should().BeTrue();
        using var target = await RedisSecretStoreSweepTarget.ConnectAsync(connectionString, configuredDatabase);

        var scan = await target.ScanAsync($"{prefix}:*", cursor: 0, count: 100);
        scan.Keys.Should().Contain(key);
        scan.Keys.Should().NotContain(defaultDatabaseKey);
        var loaded = await target.GetAsync(key);
        loaded.Should().Equal(originalValue);

        var conflict = await target.CompareExchangeAsync(key, wrongExpectedValue, updatedValue);
        conflict.Status.Should().Be(SecretStoreCasStatus.Conflict);
        var missing = await target.CompareExchangeAsync(missingKey, originalValue, updatedValue);
        missing.Status.Should().Be(SecretStoreCasStatus.Missing);
        var updated = await target.CompareExchangeAsync(key, originalValue, updatedValue);
        updated.Status.Should().Be(SecretStoreCasStatus.Updated);
        updated.PreservedTtlMs.Should().BeGreaterThan(0);

        ((byte[]?)await database.StringGetAsync(key)).Should().Equal(updatedValue);
        ((byte[]?)await defaultDatabase.StringGetAsync(key)).Should().Equal(defaultDatabaseSameKeyValue);
        (await database.KeyTimeToLiveAsync(key)).Should().NotBeNull().And.BePositive();
    }

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    private static string Base64Key(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i);

        return Convert.ToBase64String(bytes);
    }

    private static string RuntimeRecordKeyId(byte[] recordBytes) =>
        GarnetRuntimeSecretRecord.Parser.ParseFrom(recordBytes).EncryptedSecret.KeyId;

    private static Task<int> RunInjectedSweepAsync(
        ReencryptionFixture fixture,
        TextWriter output,
        TextWriter error) =>
        SecretStoreTool.MainAsync(
            [
                "reencrypt-sweep",
                "--keyring",
                fixture.NewKeyringPath,
                "--secret-vault-prefix",
                fixture.Options.SecretVaultPrefix,
                "--runtime-secret-prefix",
                fixture.Options.RuntimeSecretPrefix,
            ],
            output,
            error,
            fixture.Target);

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
            string referenceRef)
        {
            _oldKeyring = oldKeyring;
            _newKeyring = newKeyring;
            Options = options;
            Target = target;
            StoredKey = storedKey;
            ReferenceRef = referenceRef;
        }

        public string OldKeyringPath => _oldKeyring.Path;

        public string NewKeyringPath => _newKeyring.Path;

        public GarnetSecretStoreOptions Options { get; }

        public FakeSweepTarget Target { get; }

        public string StoredKey { get; }

        public string ReferenceRef { get; }

        public async Task<(string Key, string Ref)> AddRuntimeSecretAsync(string stepId, string secret) =>
            await AddRuntimeSecretAsync(Options, Target, OldKeyringPath, stepId, secret);

        public static async Task<ReencryptionFixture> CreateAsync()
        {
            var fixture = CreateEmptyCore();
            var stored = await AddRuntimeSecretAsync(
                fixture.Options,
                fixture.Target,
                fixture.OldKeyring.Path,
                "step-alpha",
                "runtime-secret");

            return new ReencryptionFixture(
                fixture.OldKeyring,
                fixture.NewKeyring,
                fixture.Options,
                fixture.Target,
                stored.Key,
                stored.Ref);
        }

        public static async Task<ReencryptionFixture> CreateVaultAsync()
        {
            var fixture = CreateEmptyCore();
            var recordingStore = new RecordingGarnetSecretKeyValueStore();
            var vault = new GarnetBackedSecretVault(
                recordingStore,
                fixture.Options,
                GarnetSecretStoreKeyring.LoadFromFile(fixture.OldKeyring.Path),
                TimeProvider.System);

            var stored = await vault.PutAsync(new StoreSecretRequest(
                "oauth-refresh-token",
                "scope-alpha",
                "user-alpha",
                "vault-secret",
                "test store vault"));
            var entry = recordingStore.Values.Should().ContainSingle().Subject;
            fixture.Target.Values[entry.Key] = entry.Value.ToArray();
            fixture.Target.Ttls[entry.Key] = recordingStore.Expirations[entry.Key];

            return new ReencryptionFixture(
                fixture.OldKeyring,
                fixture.NewKeyring,
                fixture.Options,
                fixture.Target,
                entry.Key,
                stored.Reference.Ref);
        }

        public static Task<ReencryptionFixture> CreateEmptyAsync()
        {
            var fixture = CreateEmptyCore();
            return Task.FromResult(new ReencryptionFixture(
                fixture.OldKeyring,
                fixture.NewKeyring,
                fixture.Options,
                fixture.Target,
                string.Empty,
                string.Empty));
        }

        private static async Task<(string Key, string Ref)> AddRuntimeSecretAsync(
            GarnetSecretStoreOptions options,
            FakeSweepTarget target,
            string keyringPath,
            string stepId,
            string secret)
        {
            var recordingStore = new RecordingGarnetSecretKeyValueStore();
            var runtimeStore = new GarnetRuntimeSecretStore(
                recordingStore,
                options,
                GarnetSecretStoreKeyring.LoadFromFile(keyringPath));

            var stored = await runtimeStore.PutAsync(new StoreRuntimeSecretRequest(
                "workflow-tool-token",
                "run-alpha",
                stepId,
                secret,
                TimeSpan.FromMinutes(10),
                ConsumeOnce: false,
                "test store"));
            var entry = recordingStore.Values.Should().ContainSingle().Subject;
            target.Values[entry.Key] = entry.Value.ToArray();
            target.Ttls[entry.Key] = recordingStore.Expirations[entry.Key];
            return (entry.Key, stored.Reference.Ref);
        }

        private static EmptyReencryptionFixture CreateEmptyCore()
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
            var target = new FakeSweepTarget();

            return new EmptyReencryptionFixture(oldKeyring, newKeyring, options, target);
        }

        public void Dispose()
        {
            _oldKeyring.Dispose();
            _newKeyring.Dispose();
        }

        private sealed record EmptyReencryptionFixture(
            TempFile OldKeyring,
            TempFile NewKeyring,
            GarnetSecretStoreOptions Options,
            FakeSweepTarget Target);
    }

    private sealed class FakeSweepTarget : ISecretStoreSweepTarget
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan?> Ttls { get; } = new(StringComparer.Ordinal);

        public List<string> AdditionalScanKeys { get; } = [];

        public List<FakeSweepScanRequest> ScanRequests { get; } = [];

        public SecretStoreCasResult? NextCasResult { get; set; }

        public bool StoreExpectedValueAfterUpdated { get; set; }

        public int CompareExchangeCalls { get; private set; }

        public Task<SecretStoreScanBatch> ScanAsync(
            string pattern,
            long cursor,
            int count,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScanRequests.Add(new FakeSweepScanRequest(pattern, cursor, count));
            var prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;
            var keys = Values.Keys
                .Concat(AdditionalScanKeys)
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
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
            if (NextCasResult != null)
                return Task.FromResult(NextCasResult);

            if (!Values.TryGetValue(key, out var current))
                return Task.FromResult(SecretStoreCasResult.Missing());
            if (!current.SequenceEqual(expectedValue))
                return Task.FromResult(SecretStoreCasResult.Conflict());

            Values[key] = StoreExpectedValueAfterUpdated ? expectedValue.ToArray() : newValue.ToArray();
            var ttl = Ttls.TryGetValue(key, out var existingTtl) ? existingTtl : null;
            return Task.FromResult(SecretStoreCasResult.Updated(ttl.HasValue ? (long)ttl.Value.TotalMilliseconds : -1));
        }
    }

    private sealed record FakeSweepScanRequest(string Pattern, long Cursor, int Count);

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class GarnetIntegrationFactAttribute : FactAttribute
    {
        public GarnetIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")))
            {
                Skip = "Set AEVATAR_TEST_GARNET_CONNECTION_STRING to run secret-store Garnet sweep integration tests.";
            }
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
            if (!Values.TryGetValue(key, out var current) ||
                !current.SequenceEqual(expectedValue.ToArray()))
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
            if (!Values.TryGetValue(key, out var current) ||
                !current.SequenceEqual(expectedValue.ToArray()))
            {
                return Task.FromResult(false);
            }

            Values.Remove(key);
            Expirations.Remove(key);
            return Task.FromResult(true);
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
