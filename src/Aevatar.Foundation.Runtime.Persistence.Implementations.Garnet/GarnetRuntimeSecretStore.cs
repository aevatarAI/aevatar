using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf;
using System.Security.Cryptography;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetRuntimeSecretStore : IRuntimeSecretStore
{
    private const string AtomicConsumeOnceUnsupportedMessage =
        "Garnet runtime secret consume-once requires atomic consume support and is not enabled in this implementation.";

    private readonly IGarnetSecretKeyValueStore _store;
    private readonly GarnetSecretStoreOptions _options;
    private readonly GarnetSecretStoreKeyring _keyring;
    private readonly TimeProvider _timeProvider;

    public GarnetRuntimeSecretStore(
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

    public async Task<StoreRuntimeSecretResult> PutAsync(StoreRuntimeSecretRequest request, CancellationToken ct = default)
    {
        ValidateStoreRequest(request);
        if (request.ConsumeOnce)
            throw new InvalidOperationException(AtomicConsumeOnceUnsupportedMessage);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = now + (long)request.TimeToLive.TotalMilliseconds;
        var record = new GarnetRuntimeSecretRecord
        {
            Ref = GarnetSecretRecordIds.NewSecretReference("rsec_"),
            Purpose = request.Purpose,
            OwnerRunId = request.OwnerRunId,
            OwnerStepId = request.OwnerStepId,
            ConsumeOnce = request.ConsumeOnce,
            Status = GarnetSecretRecordStatus.Active,
            Fingerprint = GarnetSecretRecordCrypto.Fingerprint(request.Secret, _keyring),
            CreatedAtUnixMs = now,
            ExpiresAtUnixMs = expiresAt,
        };
        record.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            request.Secret,
            _keyring,
            GarnetSecretRecordIds.RuntimeAssociatedData(record));

        await _store.SetAsync(BuildKey(record.Ref), record.ToByteArray(), request.TimeToLive, ct);
        return new StoreRuntimeSecretResult(ToReference(record));
    }

    public async Task<ResolveRuntimeSecretResult> ResolveAsync(
        ResolveRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ValidateResolveRequest(request);
        ct.ThrowIfCancellationRequested();

        var record = await ReadRecordAsync(request.Ref, ct);
        if (record == null ||
            !IsActive(record) ||
            !IsAuthorized(record, request.Purpose, request.OwnerRunId, request.OwnerStepId))
        {
            return new ResolveRuntimeSecretResult(null, null);
        }

        var secret = TryDecrypt(record);
        return secret == null
            ? new ResolveRuntimeSecretResult(null, null)
            : new ResolveRuntimeSecretResult(ToReference(record), secret);
    }

    public async Task<ConsumeRuntimeSecretResult> ConsumeAsync(
        ConsumeRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ValidateConsumeRequest(request);
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException(AtomicConsumeOnceUnsupportedMessage);
    }

    public async Task<RevokeRuntimeSecretResult> RevokeAsync(
        RevokeRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ValidateRevokeRequest(request);
        ct.ThrowIfCancellationRequested();

        var record = await ReadRecordAsync(request.Ref, ct);
        if (record == null ||
            record.Status == GarnetSecretRecordStatus.Revoked ||
            !IsAuthorized(record, request.Purpose, request.OwnerRunId, request.OwnerStepId))
        {
            return new RevokeRuntimeSecretResult(false);
        }

        var secret = TryDecrypt(record);
        if (secret == null)
            return new RevokeRuntimeSecretResult(false);

        var updated = record.Clone();
        updated.Status = GarnetSecretRecordStatus.Revoked;
        updated.RevokedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        updated.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            secret,
            _keyring,
            GarnetSecretRecordIds.RuntimeAssociatedData(updated));

        await _store.SetAsync(BuildKey(updated.Ref), updated.ToByteArray(), RemainingTtl(updated), ct);
        return new RevokeRuntimeSecretResult(true);
    }

    private async Task<GarnetRuntimeSecretRecord?> ReadRecordAsync(string reference, CancellationToken ct)
    {
        var bytes = await _store.GetAsync(BuildKey(reference), ct);
        return bytes == null ? null : GarnetRuntimeSecretRecord.Parser.ParseFrom(bytes);
    }

    private string BuildKey(string reference) => $"{_options.NormalizedRuntimeSecretPrefix}:{reference}";

    private bool IsActive(GarnetRuntimeSecretRecord record) =>
        record.Status == GarnetSecretRecordStatus.Active &&
        record.ExpiresAtUnixMs > _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private TimeSpan? RemainingTtl(GarnetRuntimeSecretRecord record)
    {
        var remainingMilliseconds = record.ExpiresAtUnixMs - _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return remainingMilliseconds > 0 ? TimeSpan.FromMilliseconds(remainingMilliseconds) : TimeSpan.Zero;
    }

    private string? TryDecrypt(GarnetRuntimeSecretRecord record)
    {
        try
        {
            return GarnetSecretRecordCrypto.Decrypt(
                record.EncryptedSecret,
                _keyring,
                GarnetSecretRecordIds.RuntimeAssociatedData(record));
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
        GarnetRuntimeSecretRecord record,
        string purpose,
        string ownerRunId,
        string ownerStepId) =>
        string.Equals(record.Purpose, purpose, StringComparison.Ordinal) &&
        string.Equals(record.OwnerRunId, ownerRunId, StringComparison.Ordinal) &&
        string.Equals(record.OwnerStepId, ownerStepId, StringComparison.Ordinal);

    private static RuntimeSecretReference ToReference(GarnetRuntimeSecretRecord record) => new()
    {
        Ref = record.Ref,
        Purpose = record.Purpose,
        Fingerprint = record.Fingerprint,
        ExpiresAtUnixMs = record.ExpiresAtUnixMs,
        ConsumeOnce = record.ConsumeOnce,
        OwnerRunId = record.OwnerRunId,
        OwnerStepId = record.OwnerStepId,
    };

    private static void ValidateStoreRequest(StoreRuntimeSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);
        if (request.TimeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Runtime secret TimeToLive must be positive.");
    }

    private static void ValidateResolveRequest(ResolveRuntimeSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerStepId);
    }

    private static void ValidateConsumeRequest(ConsumeRuntimeSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerStepId);
    }

    private static void ValidateRevokeRequest(RevokeRuntimeSecretRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerStepId);
    }
}
