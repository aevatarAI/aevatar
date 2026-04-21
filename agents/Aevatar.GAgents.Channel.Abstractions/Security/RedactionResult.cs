namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one sanitized payload emitted by <see cref="IPayloadRedactor"/>.
/// </summary>
/// <param name="SanitizedPayload">The payload bytes that may proceed through ingress storage or normalization.</param>
/// <param name="WasModified">Whether the redactor changed the payload contents.</param>
public sealed record RedactionResult(byte[] SanitizedPayload, bool WasModified)
{
    /// <summary>
    /// Creates a result that preserves the original payload bytes.
    /// </summary>
    public static RedactionResult Unchanged(byte[] payload) =>
        new(payload ?? throw new ArgumentNullException(nameof(payload)), false);

    /// <summary>
    /// Creates a result that carries a modified sanitized payload.
    /// </summary>
    public static RedactionResult Modified(byte[] payload) =>
        new(payload ?? throw new ArgumentNullException(nameof(payload)), true);
}
