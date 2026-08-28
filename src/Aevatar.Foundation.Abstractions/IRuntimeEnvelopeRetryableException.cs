namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Marks a failure that requires the runtime to deliver the exact current envelope again.
/// </summary>
public interface IRuntimeEnvelopeRetryableException
{
}

/// <summary>
/// Marks a transient gate failure that must remain in actor-owned durable redelivery until the
/// handler can resolve it. The transport delivery may be acknowledged only after that durable
/// continuation has accepted the exact envelope.
/// </summary>
public interface IRuntimeEnvelopeRetryUntilResolvedException
    : IRuntimeEnvelopeRetryableException
{
}

/// <summary>
/// Marks a retry-until-resolved failure whose durable continuation may supersede an older
/// continuation for the same authoritative source. The sequence must come from that source's
/// committed monotonic version, never from a local retry counter.
/// </summary>
public interface IRuntimeEnvelopeRetryCoalescingException
    : IRuntimeEnvelopeRetryUntilResolvedException
{
    RuntimeEnvelopeRetryCoalescingCursor RetryCoalescingCursor { get; }
}

/// <summary>
/// Identifies the authoritative source and committed version used to coalesce durable envelope
/// retries. A newer sequence supersedes every pending retry for the same key.
/// </summary>
public sealed record RuntimeEnvelopeRetryCoalescingCursor
{
    public RuntimeEnvelopeRetryCoalescingCursor(string key, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sequence, 0);
        Key = key;
        Sequence = sequence;
    }

    public string Key { get; }

    public long Sequence { get; }
}
