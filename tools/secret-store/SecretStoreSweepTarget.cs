namespace Aevatar.SecretStore.Tools;

public interface ISecretStoreSweepTarget
{
    Task<SecretStoreScanBatch> ScanAsync(
        string pattern,
        long cursor,
        int count,
        CancellationToken ct = default);

    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);

    Task<SecretStoreCasResult> CompareExchangeAsync(
        string key,
        byte[] expectedValue,
        byte[] newValue,
        CancellationToken ct = default);
}

public sealed record SecretStoreScanBatch(long NextCursor, IReadOnlyList<string> Keys);

public enum SecretStoreCasStatus
{
    Updated,
    Conflict,
    Missing,
}

public sealed record SecretStoreCasResult(SecretStoreCasStatus Status, long PreservedTtlMs)
{
    public static SecretStoreCasResult Updated(long preservedTtlMs) =>
        new(SecretStoreCasStatus.Updated, preservedTtlMs);

    public static SecretStoreCasResult Conflict() => new(SecretStoreCasStatus.Conflict, -1);

    public static SecretStoreCasResult Missing() => new(SecretStoreCasStatus.Missing, -2);
}
