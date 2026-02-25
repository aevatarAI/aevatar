namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionOwnershipCoordinatorOptions
{
    public const long DefaultLeaseTtlMs = 30L * 60 * 1000;
    public const long MinimumLeaseTtlMs = 1_000;
    public const long MaximumLeaseTtlMs = 24L * 60 * 60 * 1000;

    public long LeaseTtlMs { get; set; } = DefaultLeaseTtlMs;

    public long ResolveLeaseTtlMs() => NormalizeLeaseTtlMs(LeaseTtlMs);

    public static long NormalizeLeaseTtlMs(long rawLeaseTtlMs)
    {
        if (rawLeaseTtlMs <= 0)
            return DefaultLeaseTtlMs;

        return Math.Clamp(rawLeaseTtlMs, MinimumLeaseTtlMs, MaximumLeaseTtlMs);
    }
}
