namespace Aevatar.Foundation.Abstractions.Propagation;

/// <summary>
/// Reserved envelope metadata keys managed by framework-level propagation.
/// </summary>
public static class EnvelopeMetadataKeys
{
    /// <summary>
    /// Direct upstream event id for one-hop causation link.
    /// </summary>
    public const string TraceCausationId = "trace.causation_id";
}
