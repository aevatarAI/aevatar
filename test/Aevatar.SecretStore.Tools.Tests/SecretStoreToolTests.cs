using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
            fixture.ReferenceRef,
            "workflow-tool-token",
            "run-alpha",
            "step-alpha",
            "test resolve after reencrypt"));

        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("runtime-secret");
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

    [Fact]
    public async Task RedisSecretStoreSweepTarget_ShouldScanGetCompareExchangeAndPreserveTtl()
    {
        await using var redis = await LocalRedisServer.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        var prefix = $"aevatar:test:secret-sweep:{Guid.NewGuid():N}";
        var key = $"{prefix}:record";
        var missingKey = $"{prefix}:missing";
        var originalValue = Encoding.UTF8.GetBytes("old-record");
        var updatedValue = Encoding.UTF8.GetBytes("new-record");
        var wrongExpectedValue = Encoding.UTF8.GetBytes("wrong-record");

        (await database.StringSetAsync(key, originalValue, TimeSpan.FromMinutes(5))).Should().BeTrue();
        using var target = await RedisSecretStoreSweepTarget.ConnectAsync(redis.ConnectionString, database: -1);

        var scan = await target.ScanAsync($"{prefix}:*", cursor: 0, count: 100);
        scan.Keys.Should().Contain(key);
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
        (await database.KeyTimeToLiveAsync(key)).Should().NotBeNull().And.BePositive();
    }

    private static string Base64Key(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i);

        return Convert.ToBase64String(bytes);
    }

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

        public static async Task<ReencryptionFixture> CreateAsync()
        {
            var fixture = CreateEmptyCore();
            var recordingStore = new RecordingGarnetSecretKeyValueStore();
            var runtimeStore = new GarnetRuntimeSecretStore(
                recordingStore,
                fixture.Options,
                GarnetSecretStoreKeyring.LoadFromFile(fixture.OldKeyring.Path));

            var stored = await runtimeStore.PutAsync(new StoreRuntimeSecretRequest(
                "workflow-tool-token",
                "run-alpha",
                "step-alpha",
                "runtime-secret",
                TimeSpan.FromMinutes(10),
                ConsumeOnce: false,
                "test store"));
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

    private sealed class LocalRedisServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TempDirectory _directory;

        private LocalRedisServer(Process process, TempDirectory directory, int port)
        {
            _process = process;
            _directory = directory;
            ConnectionString = $"127.0.0.1:{port},abortConnect=false,connectTimeout=10000,syncTimeout=10000";
        }

        public string ConnectionString { get; }

        public static async Task<LocalRedisServer> StartAsync()
        {
            var executable = FindRedisServer();
            var directory = new TempDirectory();
            var port = GetFreeTcpPort();
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--bind");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
            startInfo.ArgumentList.Add("--save");
            startInfo.ArgumentList.Add("");
            startInfo.ArgumentList.Add("--appendonly");
            startInfo.ArgumentList.Add("no");
            startInfo.ArgumentList.Add("--dir");
            startInfo.ArgumentList.Add(directory.Path);
            startInfo.ArgumentList.Add("--dbfilename");
            startInfo.ArgumentList.Add($"secret-sweep-{Guid.NewGuid():N}.rdb");
            startInfo.ArgumentList.Add("--protected-mode");
            startInfo.ArgumentList.Add("no");

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start redis-server.");
            var server = new LocalRedisServer(process, directory, port);

            try
            {
                using var connection = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
                await connection.GetDatabase().PingAsync();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
            _directory.Dispose();
        }

        private static string FindRedisServer()
        {
            var configured = Environment.GetEnvironmentVariable("AEVATAR_TEST_REDIS_SERVER_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var fileName = OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server";
            var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var directory in pathDirectories)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }

            foreach (var candidate in new[] { "/opt/homebrew/bin/redis-server", "/usr/local/bin/redis-server", "/usr/bin/redis-server" })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new InvalidOperationException(
                "redis-server executable is required for RedisSecretStoreSweepTarget integration coverage.");
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
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
