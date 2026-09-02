using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Google.Protobuf;

namespace Aevatar.SecretStore.Tools;

public sealed class SecretStoreSweepOptions
{
    public string SecretVaultPrefix { get; set; } = "aevatar:secret-vault";

    public string RuntimeSecretPrefix { get; set; } = "aevatar:runtime-secrets";

    public bool DryRun { get; set; }

    public string? CheckpointPath { get; set; }

    public bool Resume { get; set; }

    public bool Verify { get; set; }

    public int BatchSize { get; set; } = 500;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SecretVaultPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeSecretPrefix);
        if (BatchSize <= 0)
            throw new InvalidOperationException("Secret-store reencrypt-sweep BatchSize must be positive.");
    }
}

public sealed class SecretStoreSweepResult
{
    public long Scanned { get; set; }

    public long Changed { get; set; }

    public long Updated { get; set; }

    public long AlreadyActive { get; set; }

    public long Conflicts { get; set; }

    public long Missing { get; set; }

    public long Errors { get; set; }

    public long Verified { get; set; }

    public long VerifyFailures { get; set; }

    public bool Succeeded => Conflicts == 0 && Missing == 0 && Errors == 0 && VerifyFailures == 0;
}

public sealed class SecretStoreReencryptionSweep
{
    private const string VaultPhase = "vault";
    private const string RuntimePhase = "runtime";
    private const string DonePhase = "done";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ISecretStoreSweepTarget _target;
    private readonly GarnetSecretStoreKeyring _keyring;

    public SecretStoreReencryptionSweep(ISecretStoreSweepTarget target, GarnetSecretStoreKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyring);

        _target = target;
        _keyring = keyring;
    }

    public async Task<SecretStoreSweepResult> RunAsync(
        SecretStoreSweepOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var phases = new[]
        {
            new SweepPhase(
                VaultPhase,
                NormalizePrefix(options.SecretVaultPrefix),
                GarnetSecretRecordKind.SecretVault),
            new SweepPhase(
                RuntimePhase,
                NormalizePrefix(options.RuntimeSecretPrefix),
                GarnetSecretRecordKind.RuntimeSecret),
        };
        var checkpoint = LoadCheckpoint(options);
        if (string.Equals(checkpoint.Phase, DonePhase, StringComparison.Ordinal))
            return checkpoint.Result;

        for (var phaseIndex = PhaseIndex(phases, checkpoint.Phase); phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            var cursor = string.Equals(checkpoint.Phase, phase.Name, StringComparison.Ordinal)
                ? checkpoint.Cursor
                : 0;

            do
            {
                ct.ThrowIfCancellationRequested();
                var batch = await _target.ScanAsync($"{phase.Prefix}:*", cursor, options.BatchSize, ct);
                foreach (var key in batch.Keys)
                    await ProcessKeyAsync(key, phase.Kind, options, checkpoint.Result, ct);

                cursor = batch.NextCursor;
                checkpoint.Phase = phase.Name;
                checkpoint.Cursor = cursor;
                SaveCheckpoint(options, checkpoint);
            }
            while (cursor != 0);

            checkpoint.Phase = phaseIndex + 1 == phases.Length ? DonePhase : phases[phaseIndex + 1].Name;
            checkpoint.Cursor = 0;
            SaveCheckpoint(options, checkpoint);
        }

        return checkpoint.Result;
    }

    private async Task ProcessKeyAsync(
        string key,
        GarnetSecretRecordKind kind,
        SecretStoreSweepOptions options,
        SecretStoreSweepResult result,
        CancellationToken ct)
    {
        var recordBytes = await _target.GetAsync(key, ct);
        if (recordBytes == null)
        {
            result.Missing++;
            return;
        }

        result.Scanned++;
        GarnetSecretRecordReencryptionResult reencryption;
        try
        {
            reencryption = GarnetSecretRecordReencryption.Reencrypt(recordBytes, kind, _keyring);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException or CryptographicException)
        {
            result.Errors++;
            return;
        }

        if (!reencryption.Changed)
        {
            result.AlreadyActive++;
            if (options.Verify)
                VerifyRecord(recordBytes, kind, requireActiveKey: true, result);
            return;
        }

        result.Changed++;
        if (options.DryRun)
        {
            if (options.Verify)
                VerifyRecord(reencryption.RecordBytes, kind, requireActiveKey: true, result);
            return;
        }

        var casResult = await _target.CompareExchangeAsync(key, recordBytes, reencryption.RecordBytes, ct);
        switch (casResult.Status)
        {
            case SecretStoreCasStatus.Updated:
                result.Updated++;
                if (options.Verify)
                    await VerifyStoredRecordAsync(key, kind, result, ct);
                break;
            case SecretStoreCasStatus.Conflict:
                result.Conflicts++;
                break;
            case SecretStoreCasStatus.Missing:
                result.Missing++;
                break;
            default:
                result.Errors++;
                break;
        }
    }

    private async Task VerifyStoredRecordAsync(
        string key,
        GarnetSecretRecordKind kind,
        SecretStoreSweepResult result,
        CancellationToken ct)
    {
        var storedBytes = await _target.GetAsync(key, ct);
        if (storedBytes == null)
        {
            result.VerifyFailures++;
            return;
        }

        VerifyRecord(storedBytes, kind, requireActiveKey: true, result);
    }

    private void VerifyRecord(
        byte[] recordBytes,
        GarnetSecretRecordKind kind,
        bool requireActiveKey,
        SecretStoreSweepResult result)
    {
        try
        {
            var inspection = GarnetSecretRecordReencryption.Inspect(recordBytes, kind, _keyring);
            if (requireActiveKey && !inspection.UsesActiveKey)
            {
                result.VerifyFailures++;
                return;
            }

            result.Verified++;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException or CryptographicException)
        {
            result.VerifyFailures++;
        }
    }

    private static SecretStoreSweepCheckpoint LoadCheckpoint(SecretStoreSweepOptions options)
    {
        if (!options.Resume || string.IsNullOrWhiteSpace(options.CheckpointPath) || !File.Exists(options.CheckpointPath))
            return new SecretStoreSweepCheckpoint();

        var checkpoint = JsonSerializer.Deserialize<SecretStoreSweepCheckpoint>(
            File.ReadAllText(options.CheckpointPath),
            JsonOptions);
        return checkpoint ?? new SecretStoreSweepCheckpoint();
    }

    private static void SaveCheckpoint(SecretStoreSweepOptions options, SecretStoreSweepCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(options.CheckpointPath))
            return;

        var path = Path.GetFullPath(options.CheckpointPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(checkpoint, JsonOptions));
    }

    private static int PhaseIndex(SweepPhase[] phases, string phase)
    {
        for (var i = 0; i < phases.Length; i++)
        {
            if (string.Equals(phases[i].Name, phase, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static string NormalizePrefix(string prefix) => prefix.Trim().TrimEnd(':');

    private sealed record SweepPhase(string Name, string Prefix, GarnetSecretRecordKind Kind);

    private sealed class SecretStoreSweepCheckpoint
    {
        public string Phase { get; set; } = VaultPhase;

        public long Cursor { get; set; }

        public SecretStoreSweepResult Result { get; set; } = new();
    }
}
