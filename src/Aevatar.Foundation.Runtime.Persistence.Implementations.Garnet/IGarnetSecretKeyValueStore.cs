namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

/// <summary>
/// Narrow key-value port used by Garnet-backed secret stores.
/// </summary>
public interface IGarnetSecretKeyValueStore
{
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry, CancellationToken ct = default);

    Task<bool> SetIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? expiry,
        CancellationToken ct = default);

    Task<bool> CompareSetAsync(
        string key,
        ReadOnlyMemory<byte> expectedValue,
        ReadOnlyMemory<byte> newValue,
        TimeSpan? expiry,
        CancellationToken ct = default);

    Task<bool> CompareDeleteAsync(
        string key,
        ReadOnlyMemory<byte> expectedValue,
        CancellationToken ct = default);
}
