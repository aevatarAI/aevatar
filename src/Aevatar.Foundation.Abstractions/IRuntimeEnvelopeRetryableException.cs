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
