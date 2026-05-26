namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Represents one sanitized payload emitted by <see cref="IPayloadRedactor"/>.
/// </summary>
public sealed record RedactionResult
{
    private RedactionResult(byte[] sanitizedPayload, bool wasModified)
    {
        SanitizedPayload = ClonePayload(sanitizedPayload, nameof(sanitizedPayload));
        WasModified = wasModified;
    }

    /// <summary>
    /// Gets the payload bytes that may proceed through ingress storage or normalization.
    /// </summary>
    public byte[] SanitizedPayload { get; }

    /// <summary>
    /// Gets a value indicating whether the redactor changed the payload contents.
    /// </summary>
    public bool WasModified { get; }

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

    private static byte[] ClonePayload(byte[] payload, string paramName) =>
        payload is null ? throw new ArgumentNullException(paramName) : payload.ToArray();
}
