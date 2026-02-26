namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>
/// Configuration options for Aevatar observability.
/// Controls what telemetry data is emitted.
/// </summary>
public sealed class AevatarObservabilityOptions
{
    /// <summary>
    /// When true, prompts, responses, function call arguments, and results
    /// are included in trace spans. Only enable in development/testing.
    /// Default: false.
    /// </summary>
    public bool EnableSensitiveData { get; set; }
}
