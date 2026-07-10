using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;
using System.Security.Cryptography;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetBackedSecretVault : ISecretVault
{
    private const string AtomicVersionedTransitionsUnsupportedMessage =
        "Garnet secret vault rotate/revoke requires atomic versioned transitions and is not enabled in this implementation.";

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
            ExpiresAtUnixMs = request.ExpiresAt?.ToUniversalTime().ToUnixTimeMilliseconds() ?? 0,
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

        var record = await ReadRecordAsync(request.Ref, ct);
        if (record == null ||
            record.Status != GarnetSecretRecordStatus.Active ||
            IsExpired(record) ||
            !IsAuthorized(record, request.Purpose, request.OwnerScopeKey, request.SubjectId))
        {
            return new ResolveSecretResult(null, null);
        }

        var secret = TryDecrypt(record);
        return secret == null
            ? new ResolveSecretResult(null, null)
            : new ResolveSecretResult(ToReference(record), secret);
    }

    public async Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default)
    {
        ValidateRotateRequest(request);
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw new InvalidOperationException(AtomicVersionedTransitionsUnsupportedMessage);
    }

    public async Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
    {
        ValidateRevokeRequest(request);
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw new InvalidOperationException(AtomicVersionedTransitionsUnsupportedMessage);
    }

    private async Task<GarnetSecretVaultRecord?> ReadRecordAsync(string reference, CancellationToken ct)
    {
        var bytes = await _store.GetAsync(BuildKey(reference), ct);
        return bytes == null ? null : GarnetSecretVaultRecord.Parser.ParseFrom(bytes);
    }

    private string BuildKey(string reference) => $"{_options.NormalizedSecretVaultPrefix}:{reference}";

    private string? TryDecrypt(GarnetSecretVaultRecord record)
    {
        try
        {
            return GarnetSecretRecordCrypto.Decrypt(
                record.EncryptedSecret,
                _keyring,
                GarnetSecretRecordIds.VaultAssociatedData(record));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
        ExpiresAtUnixMs = record.ExpiresAtUnixMs,
    };

    private bool IsExpired(GarnetSecretVaultRecord record) =>
        record.ExpiresAtUnixMs > 0 &&
        record.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

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
}
