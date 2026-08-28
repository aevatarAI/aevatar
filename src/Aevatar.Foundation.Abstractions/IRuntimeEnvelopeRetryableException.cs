using System.Security.Cryptography;
using Google.Protobuf;

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
/// Resolves the authoritative cursor of an envelope only after the agent handled that envelope
/// successfully. The runtime uses the cursor to complete an older durable coalesced retry; the
/// resolver must return <see langword="null"/> for envelopes it cannot authenticate as its own
/// authoritative observation.
/// </summary>
public interface IRuntimeEnvelopeRetryCoalescingCompletionSource
{
    RuntimeEnvelopeRetryCoalescingCursor? ResolveHandledRetryCoalescingCursor(
        EventEnvelope envelope);
}

/// <summary>
/// Identifies the authoritative source, committed version and deterministic committed-value
/// identity used to coalesce durable envelope retries. A newer sequence supersedes every pending
/// retry for the same key; equal sequence and precedence require the exact same committed value.
/// </summary>
public sealed record RuntimeEnvelopeRetryCoalescingCursor
{
    public RuntimeEnvelopeRetryCoalescingCursor(
        string key,
        long sequence,
        string valueIdentity,
        int precedence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sequence, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(precedence);
        Key = key;
        Sequence = sequence;
        ValueIdentity = valueIdentity;
        Precedence = precedence;
    }

    public string Key { get; }

    public long Sequence { get; }

    public string ValueIdentity { get; }

    public int Precedence { get; }

    public static RuntimeEnvelopeRetryCoalescingComparison Compare(
        RuntimeEnvelopeRetryCoalescingCursor existing,
        RuntimeEnvelopeRetryCoalescingCursor incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        if (!string.Equals(existing.Key, incoming.Key, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot compare runtime retry coalescing cursors for '{existing.Key}' and '{incoming.Key}'.");
        }

        if (incoming.Sequence < existing.Sequence)
            return RuntimeEnvelopeRetryCoalescingComparison.Stale;
        if (incoming.Sequence > existing.Sequence)
            return RuntimeEnvelopeRetryCoalescingComparison.Superseding;
        if (incoming.Precedence < existing.Precedence)
            return RuntimeEnvelopeRetryCoalescingComparison.Stale;
        if (incoming.Precedence > existing.Precedence)
            return RuntimeEnvelopeRetryCoalescingComparison.Superseding;
        return string.Equals(existing.ValueIdentity, incoming.ValueIdentity, StringComparison.Ordinal)
            ? RuntimeEnvelopeRetryCoalescingComparison.Exact
            : RuntimeEnvelopeRetryCoalescingComparison.Conflict;
    }
}

public enum RuntimeEnvelopeRetryCoalescingComparison
{
    Exact = 0,
    Stale = 1,
    Superseding = 2,
    Conflict = 3,
}

/// <summary>
/// Builds the stable committed-value identity carried by a retry coalescing cursor. Deterministic
/// protobuf serialization makes semantically identical map-bearing messages compare byte-for-byte
/// across processes and rolling binaries.
/// </summary>
public static class RuntimeEnvelopeRetryCoalescingValueIdentity
{
    public static string Create(IMessage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream(value.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true })
        {
            value.WriteTo(output);
            output.Flush();
        }

        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
