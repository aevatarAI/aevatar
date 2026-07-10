using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;
using System.Security.Cryptography;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetBackedSecretVault : ISecretVault
{
    private readonly IGarnetSecretKeyValueStore _store;
    private readonly GarnetSecretStoreOptions _options;
    private readonly GarnetSecretStoreKeyring _keyring;
    private readonly TimeProvider _timeProvider;

    public GarnetBackedSecretVault(
        IGarnetSecretKeyValueStore store,
        GarnetSecretStoreOptions options,
        GarnetSecretStoreKeyring keyring,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyring);
        options.Validate();

        _store = store;
        _options = options;
        _keyring = keyring;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
    {
        ValidateStoreRequest(request);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new GarnetSecretVaultRecord
        {
            Ref = GarnetSecretRecordIds.NewSecretReference("sec_"),
            Purpose = request.Purpose,
            OwnerScopeKey = request.OwnerScopeKey,
            SubjectId = request.SubjectId,
            Version = 1,
            Status = GarnetSecretRecordStatus.Active,
            Fingerprint = GarnetSecretRecordCrypto.Fingerprint(request.Secret, _keyring),
            CreatedAtUnixMs = now,
        };
        record.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            request.Secret,
            _keyring,
            GarnetSecretRecordIds.VaultAssociatedData(record));

        await _store.SetAsync(BuildKey(record.Ref), record.ToByteArray(), expiry: null, ct);
        return new StoreSecretResult(ToReference(record));
    }

    public async Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
    {
        ValidateResolveRequest(request);
        ct.ThrowIfCancellationRequested();

        var read = await ReadRecordAsync(request.Ref, ct);
        if (read.FailureReason is not SecretResolutionFailureReason.None)
        {
            return new ResolveSecretResult(null, null, read.FailureReason);
        }

        var record = read.Record!;
        if (record.Status != GarnetSecretRecordStatus.Active)
        {
            return new ResolveSecretResult(null, null, SecretResolutionFailureReason.Revoked);
        }

        if (!IsAuthorized(record, request.Purpose, request.OwnerScopeKey, request.SubjectId))
        {
            return new ResolveSecretResult(null, null, SecretResolutionFailureReason.Unauthorized);
        }

        var secret = TryDecrypt(record);
        return secret.FailureReason == SecretResolutionFailureReason.None
            ? new ResolveSecretResult(ToReference(record), secret.Secret)
            : new ResolveSecretResult(null, null, secret.FailureReason);
    }

    public async Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default)
    {
        ValidateRotateRequest(request);
        ct.ThrowIfCancellationRequested();

        var read = await ReadRecordBytesAsync(request.Ref, ct);
        var record = EnsureActiveAuthorizedRecord(
            read,
            request.Purpose,
            request.OwnerScopeKey,
            request.SubjectId,
            "rotate");

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = record.Clone();
        updated.Version++;
        updated.Fingerprint = GarnetSecretRecordCrypto.Fingerprint(request.Secret, _keyring);
        updated.RotatedAtUnixMs = now;
        updated.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            request.Secret,
            _keyring,
            GarnetSecretRecordIds.VaultAssociatedData(updated));

        var replaced = await _store.CompareSetAsync(
            BuildKey(record.Ref),
            read.Bytes!,
            updated.ToByteArray(),
            ct);
        if (!replaced)
            throw new InvalidOperationException("Secret reference changed before rotate could be committed.");

        return new RotateSecretResult(ToReference(updated));
    }

    public async Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
    {
        ValidateRevokeRequest(request);
        ct.ThrowIfCancellationRequested();

        var read = await ReadRecordBytesAsync(request.Ref, ct);
        if (read.Record is null || read.FailureReason is not SecretResolutionFailureReason.None)
            return new RevokeSecretResult(false);

        var record = read.Record;
        if (record.Status != GarnetSecretRecordStatus.Active ||
            !IsAuthorized(record, request.Purpose, request.OwnerScopeKey, request.SubjectId))
        {
            return new RevokeSecretResult(false);
        }

        var revoked = await _store.CompareDeleteAsync(
            BuildKey(record.Ref),
            read.Bytes!,
            ct);
        return new RevokeSecretResult(revoked);
    }

    private async Task<ReadVaultRecordResult> ReadRecordAsync(string reference, CancellationToken ct)
    {
        var read = await ReadRecordBytesAsync(reference, ct);
        return new ReadVaultRecordResult(read.Record, read.FailureReason);
    }

    private async Task<ReadVaultRecordBytesResult> ReadRecordBytesAsync(string reference, CancellationToken ct)
    {
        var bytes = await _store.GetAsync(BuildKey(reference), ct);
        if (bytes == null)
            return new ReadVaultRecordBytesResult(null, null, SecretResolutionFailureReason.NotFound);

        try
        {
            return new ReadVaultRecordBytesResult(
                GarnetSecretVaultRecord.Parser.ParseFrom(bytes),
                bytes,
                SecretResolutionFailureReason.None);
        }
        catch (InvalidProtocolBufferException)
        {
            return new ReadVaultRecordBytesResult(null, bytes, SecretResolutionFailureReason.InvalidRecord);
        }
    }

    private string BuildKey(string reference) => $"{_options.NormalizedSecretVaultPrefix}:{reference}";

    private DecryptVaultRecordResult TryDecrypt(GarnetSecretVaultRecord record)
    {
        try
        {
            return new DecryptVaultRecordResult(
                GarnetSecretRecordCrypto.Decrypt(
                    record.EncryptedSecret,
                    _keyring,
                    GarnetSecretRecordIds.VaultAssociatedData(record)),
                SecretResolutionFailureReason.None);
        }
        catch (CryptographicException)
        {
            return new DecryptVaultRecordResult(null, SecretResolutionFailureReason.AuthenticationFailed);
        }
        catch (InvalidOperationException ex)
        {
            return new DecryptVaultRecordResult(
                null,
                ex.Message.Contains("keyring does not contain key", StringComparison.Ordinal)
                    ? SecretResolutionFailureReason.KeyringMismatch
                    : SecretResolutionFailureReason.UnsupportedAlgorithm);
        }
    }

    private static GarnetSecretVaultRecord EnsureActiveAuthorizedRecord(
        ReadVaultRecordBytesResult read,
        string purpose,
        string ownerScopeKey,
        string subjectId,
        string operation)
    {
        if (read.Record is null || read.FailureReason is not SecretResolutionFailureReason.None)
            throw new InvalidOperationException($"Secret reference is not available for {operation}: {read.FailureReason}.");

        var record = read.Record;
        if (record.Status != GarnetSecretRecordStatus.Active ||
            !IsAuthorized(record, purpose, ownerScopeKey, subjectId))
        {
            throw new InvalidOperationException($"Secret reference is not active for the requested owner and purpose during {operation}.");
        }

        return record;
    }

    private static bool IsAuthorized(
        GarnetSecretVaultRecord record,
        string purpose,
        string ownerScopeKey,
        string subjectId) =>
        string.Equals(record.Purpose, purpose, StringComparison.Ordinal) &&
        string.Equals(record.OwnerScopeKey, ownerScopeKey, StringComparison.Ordinal) &&
        string.Equals(record.SubjectId, subjectId, StringComparison.Ordinal);

    private static SecretReference ToReference(GarnetSecretVaultRecord record) => new()
    {
        Ref = record.Ref,
        Purpose = record.Purpose,
        Fingerprint = record.Fingerprint,
        Version = record.Version,
        OwnerScopeKey = record.OwnerScopeKey,
        CreatedAtUnixMs = record.CreatedAtUnixMs,
    };

    private static void ValidateStoreRequest(StoreSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);
    }

    private static void ValidateResolveRequest(ResolveSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
    }

    private static void ValidateRotateRequest(RotateSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);
    }

    private static void ValidateRevokeRequest(RevokeSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
    }

    private readonly record struct ReadVaultRecordResult(
        GarnetSecretVaultRecord? Record,
        SecretResolutionFailureReason FailureReason);

    private readonly record struct ReadVaultRecordBytesResult(
        GarnetSecretVaultRecord? Record,
        byte[]? Bytes,
        SecretResolutionFailureReason FailureReason);

    private readonly record struct DecryptVaultRecordResult(
        string? Secret,
        SecretResolutionFailureReason FailureReason);
}
