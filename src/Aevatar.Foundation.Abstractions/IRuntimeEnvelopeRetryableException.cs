namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Marks a failure that requires the runtime to redeliver the current envelope.
/// </summary>
public interface IRuntimeEnvelopeRetryableException
{
}
